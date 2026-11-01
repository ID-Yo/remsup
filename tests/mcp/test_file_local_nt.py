import hashlib
import os
import pathlib
import sys
import tempfile
import unittest
import asyncio
from unittest.mock import patch

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[2] / 'skills/remsup-support/scripts'))
import file_local

class FileLocalNtTests(unittest.TestCase):
    def setUp(self):
        self.dir = tempfile.TemporaryDirectory(dir='E:/Codex_work')
        self.root = pathlib.Path(self.dir.name)

    def tearDown(self):
        self.dir.cleanup()

    def test_bound_file_upload_and_download(self):
        source = self.root / 'source.bin'
        data = b'hello world from python nt binding'
        source.write_bytes(data)

        bound_up = file_local.BoundFile(str(source), download=False)
        read_data = bound_up.file.read()
        bound_up.close()
        self.assertEqual(read_data, data)

        dest = self.root / 'downloaded.bin'
        bound_down = file_local.BoundFile(str(dest), download=True, overwrite=False)
        bound_down.file.write(data)
        bound_down.commit()
        bound_down.close()
        self.assertEqual(dest.read_bytes(), data)

    def test_download_cancellation_cleans_partial(self):
        dest = self.root / 'cancelled.bin'
        bound_down = file_local.BoundFile(str(dest), download=True, overwrite=False)
        bound_down.file.write(b'partial junk')
        bound_down.close()  # not committed
        self.assertFalse(dest.exists())
        partials = list(self.root.glob('.remsup-*.partial'))
        self.assertEqual(len(partials), 0)

    def test_upload_chunk_returns_server_refusal_and_cancels(self):
        import httpx
        source=self.root / 'source.bin'
        source.write_bytes(b'synthetic')
        calls=[]
        async def call(name,args):
            calls.append(name)
            if name=='remsup_file_begin':return {'transfer_id':'fixture','size':9,'sha256':hashlib.sha256(b'synthetic').hexdigest()}
            return {}
        async def run():
            transport=httpx.MockTransport(lambda request:httpx.Response(409,json={'error_code':'interrupted'}))
            async with httpx.AsyncClient(transport=transport) as http:
                with self.assertRaisesRegex(file_local.LocalFileError,'^interrupted$'):
                    await file_local.transfer({'operation':'upload','local_path':str(source),'remote_path':'C:\\fixture','device_id':'111222333','otp':'synthetic'},call,http,'http://localhost/mcp',lambda _:None)
        asyncio.run(run())
        self.assertEqual(calls,['remsup_file_begin','remsup_file_cancel'])

    def test_download_chunk_returns_server_refusal_and_cleans_partial(self):
        import httpx
        dest=self.root / 'download.bin'
        calls=[]
        async def call(name,args):
            calls.append(name)
            if name=='remsup_file_begin':return {'transfer_id':'fixture','size':9,'sha256':hashlib.sha256(b'synthetic').hexdigest()}
            return {}
        async def run():
            transport=httpx.MockTransport(lambda request:httpx.Response(409,json={'error_code':'interrupted'}))
            async with httpx.AsyncClient(transport=transport) as http:
                with self.assertRaisesRegex(file_local.LocalFileError,'^interrupted$'):
                    await file_local.transfer({'operation':'download','local_path':str(dest),'remote_path':'C:\\fixture','device_id':'111222333','otp':'synthetic'},call,http,'http://localhost/mcp',lambda _:None)
        asyncio.run(run())
        self.assertFalse(dest.exists())
        self.assertFalse(list(self.root.glob('.remsup-*.partial')))
        self.assertEqual(calls,['remsup_file_begin','remsup_file_cancel'])

if __name__ == '__main__':
    unittest.main()
