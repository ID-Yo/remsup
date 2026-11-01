"""Bounded raw-byte file subprotocol. Model-facing tools never carry payloads."""
import base64
import hashlib
import socket
import threading
import time
import uuid
from remsup_relay_client import open_operator_socket, _recv_bounded_line, validate_diagnostics_credentials

CHUNK = 65536
MAX_SIZE = 1073741824
ERRORS = frozenset(('permission-required','permission-revoked','access-denied','parent-missing','source-missing','invalid-path','reparse-path','path-unavailable','partial-create-failed','insufficient-space','invalid-metadata','invalid-offset','invalid-request','invalid-chunk','size-mismatch','integrity-failed','source-changed','destination-exists','destination-changed','interrupted','transfer-timeout','cancelled','busy','unsupported-transfer','invalid-response','outcome-unknown','device-unavailable','owner-mismatch','capacity-full','transfer-expired'))

class TransferError(Exception):
    def __init__(self, code):
        self.code = code if code in ERRORS else 'interrupted'
        super().__init__(self.code)

def metadata(size, sha):
    if type(size) is not int or not 0 <= size <= MAX_SIZE or not isinstance(sha,str) or len(sha)!=64 or any(c not in '0123456789abcdef' for c in sha):
        raise TransferError('invalid-metadata')

class FileWire:
    def __init__(self, sock):
        self.sock=sock
        self.sock.settimeout(20)
        self.sock.setsockopt(socket.IPPROTO_TCP,socket.TCP_NODELAY,1)
    def send(self, value): self.sock.sendall((value+'\n').encode('ascii'))
    def response(self):
        try: parts=_recv_bounded_line(self.sock,8192,20).decode('ascii',errors='strict').split(' ')
        except (OSError,ValueError,RuntimeError): raise TransferError('interrupted') from None
        if len(parts)==2 and parts[0]=='ERROR': raise TransferError(parts[1])
        return parts
    def begin(self, direction,path,size=None,sha=None,overwrite=False):
        if not isinstance(path,str) or not path or len(path)>1024 or '\x00' in path: raise TransferError('invalid-path')
        self.send('REMSUPFILE1')
        if self.response()!=['REMSUPFILE1','READY','65536','1073741824']: raise TransferError('unsupported-transfer')
        encoded=base64.b64encode(path.encode('utf-8')).decode('ascii')
        if direction=='upload':
            metadata(size,sha);self.send(f'PUT {encoded} {size} {sha} {int(overwrite)}')
            if self.response()!=['ACK','0']:raise TransferError('invalid-response')
        elif direction=='download':
            self.send('GET '+encoded);parts=self.response()
            if len(parts)!=3 or parts[0]!='META':raise TransferError('invalid-response')
            try:size=int(parts[1]);sha=parts[2];metadata(size,sha)
            except (ValueError,TypeError):raise TransferError('invalid-response') from None
        else:raise TransferError('invalid-metadata')
        return size,sha
    def upload(self,offset,data):
        if not isinstance(data,bytes) or not 1<=len(data)<=CHUNK:raise TransferError('invalid-chunk')
        self.send(f'DATA {offset} {len(data)}');self.sock.sendall(data)
        if self.response()!=['ACK',str(offset+len(data))]:raise TransferError('invalid-response')
    def download(self,offset,count):
        if type(count) is not int or not 1<=count<=CHUNK:raise TransferError('invalid-chunk')
        self.send(f'READ {offset} {count}')
        if self.response()!=['DATA',str(offset),str(count)]:raise TransferError('invalid-response')
        data=bytearray();deadline=time.monotonic()+20
        while len(data)<count:
            remaining=deadline-time.monotonic()
            if remaining<=0:raise TransferError('transfer-timeout')
            self.sock.settimeout(remaining);block=self.sock.recv(count-len(data))
            if not block:raise TransferError('interrupted')
            data.extend(block)
        return bytes(data)
    def finish(self,size,sha):
        self.send('COMMIT')
        if self.response()!=['DONE',str(size),sha]:raise TransferError('invalid-response')
    def close(self):
        try:self.sock.shutdown(socket.SHUT_RDWR)
        except OSError:pass
        self.sock.close()

class Transfers:
    """Caller-owned socket registry; global lock never covers network I/O."""
    def __init__(self,connect=None,clock=time.monotonic):
        self.connect=connect;self.clock=clock;self.lock=threading.Lock();self.items={}
    def begin(self,caller,device_id,otp,direction,path,size=None,sha=None,overwrite=False):
        device=validate_diagnostics_credentials(device_id,otp)
        if type(overwrite) is not bool or direction not in ('upload','download'):raise TransferError('invalid-metadata')
        if direction=='upload':metadata(size,sha)
        identifier=str(uuid.uuid4());now=self.clock()
        entry=dict(caller=caller,device=device,direction=direction,state='Connecting',size=0,sha='',offset=0,wire=None,gate=threading.Lock(),created=now,touched=now,cancelled=False,error='')
        with self.lock:
            terminal=[(key,x) for key,x in self.items.items() if x['state'] in ('Complete','Cancelled','Failed')]
            for key,_ in sorted(terminal,key=lambda item:item[1]['touched'])[:-16]:self.items.pop(key,None)
            active=[x for x in self.items.values() if x['state'] not in ('Complete','Cancelled','Failed')]
            if len(active)>=4 or sum(x['caller']==caller for x in active)>=2:raise TransferError('capacity-full')
            if any(x['device']==device for x in active):raise TransferError('busy')
            self.items[identifier]=entry
        try:
            wire=self.connect(device,otp)
            with self.lock:
                entry['wire']=wire
                if entry['cancelled']:wire.close();raise TransferError('cancelled')
            actual_size,actual_sha=wire.begin(direction,path,size,sha,overwrite)
            with self.lock:
                if entry['cancelled']:raise TransferError('cancelled')
                entry.update(size=actual_size,sha=actual_sha,state='Active',touched=self.clock())
            return self.status(caller,identifier)
        except Exception as ex:
            self._fail(entry,ex);raise TransferError(entry['error']) from None
    def _entry(self,caller,identifier):
        with self.lock:
            entry=self.items.get(identifier)
            if entry is None or entry['caller']!=caller:raise TransferError('owner-mismatch')
            return entry
    def _fail(self,entry,ex):
        with self.lock:entry.update(state='Failed',error=ex.code if isinstance(ex,TransferError) else 'interrupted');wire=entry['wire']
        if wire:wire.close()
    def status(self,caller,identifier):
        entry=self._entry(caller,identifier)
        with self.lock:return dict(transfer_id=identifier,state=entry['state'],direction=entry['direction'],bytes=entry['offset'],size=entry['size'],sha256=entry['sha'],error_code=entry['error'])
    def chunk(self,caller,identifier,offset,data=None,count=None):
        entry=self._entry(caller,identifier)
        if not entry['gate'].acquire(False):raise TransferError('busy')
        try:
            with self.lock:
                if entry['state']!='Active' or entry['cancelled']:raise TransferError('cancelled')
                if type(offset) is not int or offset!=entry['offset']:raise TransferError('invalid-offset')
                remaining=entry['size']-offset;wire=entry['wire']
            if entry['direction']=='upload':
                if not isinstance(data,bytes) or not 1<=len(data)<=min(CHUNK,remaining):raise TransferError('invalid-chunk')
                wire.upload(offset,data);result=None;length=len(data)
            else:
                if type(count) is not int or not 1<=count<=min(CHUNK,remaining):raise TransferError('invalid-chunk')
                result=wire.download(offset,count);length=len(result)
            with self.lock:
                if entry['cancelled']:raise TransferError('cancelled')
                entry['offset']+=length;entry['touched']=self.clock()
            return result
        except Exception as ex:self._fail(entry,ex);raise TransferError(entry['error']) from None
        finally:entry['gate'].release()
    def finish(self,caller,identifier):
        entry=self._entry(caller,identifier)
        if not entry['gate'].acquire(False):raise TransferError('busy')
        try:
            with self.lock:
                if entry['state']!='Active' or entry['cancelled']:raise TransferError('cancelled')
                if entry['offset']!=entry['size']:raise TransferError('size-mismatch')
                entry['state']='Committing';wire=entry['wire']
            # Once COMMIT may be sent, cancellation is never proof of no placement.
            try:wire.finish(entry['size'],entry['sha'])
            except TransferError as ex:
                if ex.code in ('interrupted','invalid-response','transfer-timeout'):raise TransferError('outcome-unknown') from None
                raise
            except Exception:raise TransferError('outcome-unknown') from None
            with self.lock:entry.update(state='Complete',error='',touched=self.clock())
            wire.close();return self.status(caller,identifier)
        except Exception as ex:self._fail(entry,ex);raise TransferError(entry['error']) from None
        finally:entry['gate'].release()
    def cancel(self,caller,identifier):
        entry=self._entry(caller,identifier)
        with self.lock:
            if entry['state']=='Complete':return self.status_unlocked(identifier,entry)
            unknown=entry['state']=='Committing';entry.update(cancelled=True,state='Failed' if unknown else 'Cancelled',error='outcome-unknown' if unknown else 'cancelled',touched=self.clock());wire=entry['wire']
        if wire:wire.close()
        return self.status(caller,identifier)
    def status_unlocked(self,identifier,entry):return dict(transfer_id=identifier,state=entry['state'],direction=entry['direction'],bytes=entry['offset'],size=entry['size'],sha256=entry['sha'],error_code=entry['error'])
    def cleanup(self,all=False):
        now=self.clock()
        with self.lock:expired=[(key,e['caller']) for key,e in self.items.items() if all or now-e['touched']>60 or now-e['created']>1800]
        for key,caller in expired:
            self.cancel(caller,key)
            with self.lock:self.items.pop(key,None)
