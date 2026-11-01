"""Windows-local streamed file handles; no payload is returned to MCP/model text."""
import asyncio
import ctypes
from ctypes import wintypes
import hashlib
import json
import msvcrt
import os
import pathlib
import struct
import uuid
from urllib.parse import urlsplit,urlunsplit

CHUNK=65536
class LocalFileError(Exception):pass

async def chunk_error(response):
    body=bytearray()
    async for block in response.aiter_bytes():
        if len(body)+len(block)>4096:raise LocalFileError('remote-chunk-failed')
        body.extend(block)
    try:
        value=json.loads(body)
        code=value.get('error_code') if isinstance(value,dict) else None
    except (ValueError,UnicodeError):code=None
    raise LocalFileError(code if isinstance(code,str) else 'remote-chunk-failed')

kernel=ctypes.WinDLL('kernel32',use_last_error=True)
ntdll=ctypes.WinDLL('ntdll')

class UnicodeName(ctypes.Structure):
    _fields_=[('Length',wintypes.USHORT),('MaximumLength',wintypes.USHORT),('Buffer',wintypes.LPWSTR)]
class ObjectAttributes(ctypes.Structure):
    _fields_=[('Length',wintypes.ULONG),('RootDirectory',wintypes.HANDLE),('ObjectName',ctypes.POINTER(UnicodeName)),('Attributes',wintypes.ULONG),('SecurityDescriptor',wintypes.LPVOID),('SecurityQualityOfService',wintypes.LPVOID)]
class IoStatusBlock(ctypes.Structure):
    _fields_=[('Status',ctypes.c_void_p),('Information',ctypes.c_void_p)]

NtCreateFile=ntdll.NtCreateFile
NtCreateFile.argtypes=[ctypes.POINTER(wintypes.HANDLE),wintypes.DWORD,ctypes.POINTER(ObjectAttributes),ctypes.POINTER(IoStatusBlock),ctypes.c_void_p,wintypes.ULONG,wintypes.ULONG,wintypes.ULONG,wintypes.ULONG,ctypes.c_void_p,wintypes.ULONG]
NtCreateFile.restype=ctypes.c_int

NtSetInformationFile=ntdll.NtSetInformationFile
NtSetInformationFile.argtypes=[wintypes.HANDLE,ctypes.POINTER(IoStatusBlock),ctypes.c_void_p,wintypes.ULONG,ctypes.c_int]
NtSetInformationFile.restype=ctypes.c_int

kernel.CreateFileW.argtypes=[wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
kernel.CreateFileW.restype=wintypes.HANDLE
kernel.CloseHandle.argtypes=[wintypes.HANDLE]
kernel.GetFileInformationByHandle.argtypes=[wintypes.HANDLE,ctypes.c_void_p]
INVALID=ctypes.c_void_p(-1).value

def verify_handle(handle,directory=True):
    info=ctypes.create_string_buffer(52)
    if not kernel.GetFileInformationByHandle(handle,info):raise LocalFileError('local-handle-query-failed')
    attrs=struct.unpack_from('<I',info.raw)[0]
    if attrs&0x400 or (directory and not (attrs&0x10)):raise LocalFileError('local-reparse-path')

def relative_open(parent,name,access,share,disposition,directory=False):
    if not name or any(c in name for c in ('\\','/',':')) or name in ('.','..'):
        raise LocalFileError('invalid-local-path')
    uname=UnicodeName(len(name)*2,(len(name)+1)*2,name)
    attrs=ObjectAttributes(ctypes.sizeof(ObjectAttributes),parent,ctypes.pointer(uname),0x40,None,None)
    io=IoStatusBlock()
    handle=wintypes.HANDLE()
    options=0x200020|(1 if directory else 0x40)
    res=NtCreateFile(ctypes.byref(handle),access|0x100000,ctypes.byref(attrs),ctypes.byref(io),None,0,share,disposition,options,None,0)
    if res<0:
        if res==ctypes.c_int(0xc0000035).value:raise LocalFileError('destination-exists')
        if res==ctypes.c_int(0xc0000034).value:raise LocalFileError('source-missing')
        raise LocalFileError('local-path-unavailable')
    try:verify_handle(handle,directory);return handle
    except BaseException:kernel.CloseHandle(handle);raise

class BoundFile:
    def __init__(self,path,download=False,overwrite=False):
        raw=str(path)
        if len(raw)<3 or not raw[0].isalpha() or raw[1:3] not in (':\\',':/') or ':' in raw[2:]:raise LocalFileError('invalid-local-path')
        self.path=pathlib.Path(os.path.abspath(raw));self.parents=[];self.file=None;self.committed=not download;self.download=download;self.replace=False
        try:
            root=str(self.path.anchor)
            root_handle=kernel.CreateFileW(root,0x80,3,None,3,0x02200000,None)
            if root_handle==INVALID:raise LocalFileError('local-path-unavailable')
            self.parents.append(root_handle)
            verify_handle(root_handle,True)
            for part in self.path.parent.relative_to(self.path.anchor).parts:
                self.verify_ancestors()
                self.parents.append(relative_open(self.parents[-1],part,0x80,3,1,directory=True))
            self.verify_ancestors()
            leaf=self.path.name
            if download:
                self.replace=self.path.exists()
                if self.replace and not overwrite:raise LocalFileError('destination-exists')
                self.temp_name='.remsup-'+uuid.uuid4().hex+'.partial'
                handle=relative_open(self.parents[-1],self.temp_name,0xc0010000,0,2,directory=False)
                self.file=os.fdopen(msvcrt.open_osfhandle(handle.value,os.O_BINARY|os.O_RDWR),'w+b',buffering=CHUNK)
            else:
                handle=relative_open(self.parents[-1],leaf,0x80000000,1,1,directory=False)
                self.file=os.fdopen(msvcrt.open_osfhandle(handle.value,os.O_BINARY|os.O_RDONLY),'rb',buffering=CHUNK)
        except BaseException:self.close();raise
    def verify_ancestors(self):
        for p in self.parents:verify_handle(p,True)
    def commit(self):
        self.verify_ancestors()
        self.file.flush();os.fsync(self.file.fileno())
        name=self.path.name.encode('utf-16le')
        offset=20 if struct.calcsize('P')==8 else 12
        data=ctypes.create_string_buffer(offset+len(name)+2)
        data[0]=int(self.replace).to_bytes(1,'little')
        struct.pack_into('P',data,8 if offset==20 else 4,self.parents[-1].value)
        struct.pack_into('<I',data,16 if offset==20 else 8,len(name))
        ctypes.memmove(ctypes.addressof(data)+offset,name,len(name))
        io=IoStatusBlock()
        res=NtSetInformationFile(msvcrt.get_osfhandle(self.file.fileno()),ctypes.byref(io),data,len(data),10)
        if res<0:raise LocalFileError(f'local-finalize-failed: ntstatus={hex(res&0xffffffff)}')
        self.committed=True
    def close(self):
        try:
            if self.file is not None:
                if self.download and not self.committed:
                    flag=ctypes.c_ubyte(1)
                    kernel.SetFileInformationByHandle(msvcrt.get_osfhandle(self.file.fileno()),4,ctypes.byref(flag),1)
                self.file.close();self.file=None
        finally:
            for p in reversed(self.parents):kernel.CloseHandle(p)
            self.parents=[]

async def transfer(request,call,http,endpoint,emit):
    direction=request['operation'];bound=BoundFile(request['local_path'],direction=='download',request.get('overwrite',False));identifier=None;success=False
    async def tool(name,args):
        result=await call(name,args)
        if result.get('status')=='error':raise LocalFileError(result.get('error_code','transfer-failed'))
        return result
    try:
        arguments=dict(device_id=request['device_id'],otp=request['otp'],direction=direction,remote_path=request['remote_path'],overwrite=request.get('overwrite',False))
        if direction=='upload':
            digest=hashlib.sha256();size=0
            while True:
                data=await asyncio.to_thread(bound.file.read,CHUNK)
                if not data:break
                size+=len(data)
                if size>1073741824:raise LocalFileError('file-too-large')
                digest.update(data)
            bound.file.seek(0);arguments.update(size=size,sha256=digest.hexdigest())
        result=await tool('remsup_file_begin',arguments);identifier=result['transfer_id'];size=result['size'];sha=result['sha256'];offset=0;digest=hashlib.sha256()
        if size>1073741824 or size<0:raise LocalFileError('invalid-transfer-metadata')
        parsed=urlsplit(endpoint);url=urlunsplit((parsed.scheme,parsed.netloc,'/files/'+identifier,'',''))
        while offset<size:
            count=min(CHUNK,size-offset)
            if direction=='upload':
                data=await asyncio.to_thread(bound.file.read,count)
                if len(data)!=count:raise LocalFileError('local-source-changed')
                response=await http.post(url,params={'offset':offset},content=data,headers={'Content-Type':'application/octet-stream'})
            else:
                async with http.stream('GET',url,params={'offset':offset,'count':count}) as response:
                    if response.status_code!=200:await chunk_error(response)
                    data=bytearray()
                    async for block in response.aiter_raw(CHUNK):
                        if len(data)+len(block)>count:raise LocalFileError('invalid-chunk')
                        data.extend(block)
                if len(data)!=count:raise LocalFileError('incomplete-chunk')
                bound.file.write(data);digest.update(data)
            if response.status_code!=200:await chunk_error(response)
            offset+=count
            if offset==size or offset%(1024*1024)==0:emit({'transfer_id':identifier,'bytes':offset,'size':size,'state':'Transferring'})
        if direction=='download' and digest.hexdigest()!=sha:raise LocalFileError('integrity-failed')
        completed=await tool('remsup_file_finish',{'transfer_id':identifier})
        if completed.get('state')!='Complete' or completed.get('size')!=size or completed.get('sha256')!=sha:raise LocalFileError('integrity-failed')
        if direction=='download':bound.commit()
        success=True;return {'transfer_id':identifier,'state':'Complete','bytes':size,'size':size,'sha256':sha}
    finally:
        if identifier and not success:
            try:await call('remsup_file_cancel',{'transfer_id':identifier})
            except Exception:pass
        bound.close()
