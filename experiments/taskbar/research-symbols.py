"""Read-only PE/PDB research helper. Never injects or writes target memory.

Uses the exact PE CodeView identity to fetch Microsoft's public PDB, then uses
DbgHelp in this Python process to report symbol RVAs. PDB files stay under out/.
No third-party Python dependency is required. Windows x64 Python is required.
"""
from __future__ import annotations

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import struct
import urllib.request
import uuid


SYMBOL_FILTERS = (
    "?GetTaskbarHost@CTaskBand@@",
    "?FrameHeight@TaskbarHost@@",
    "?_Decref@_Ref_count_base@std@@",
    "??_7CTaskBand@@6BITaskListWndSite@@@",
)


class PeFile:
    def __init__(self, path: Path):
        self.path = path.resolve()
        self.data = self.path.read_bytes()
        if self.data[:2] != b"MZ":
            raise ValueError("not a PE file")
        self.pe = struct.unpack_from("<I", self.data, 0x3C)[0]
        if self.data[self.pe:self.pe + 4] != b"PE\0\0":
            raise ValueError("invalid PE signature")
        self.machine, self.section_count = struct.unpack_from("<HH", self.data, self.pe + 4)
        self.optional = self.pe + 24
        magic = struct.unpack_from("<H", self.data, self.optional)[0]
        if magic not in (0x10B, 0x20B):
            raise ValueError("unsupported optional header")
        self.directories = self.optional + (112 if magic == 0x20B else 96)
        self.sections = self.optional + struct.unpack_from("<H", self.data, self.pe + 20)[0]

    def offset(self, rva: int) -> int:
        for i in range(self.section_count):
            entry = self.sections + 40 * i
            virtual_size, virtual_address, raw_size, raw_pointer = struct.unpack_from(
                "<IIII", self.data, entry + 8)
            if virtual_address <= rva < virtual_address + max(virtual_size, raw_size):
                return raw_pointer + rva - virtual_address
        raise ValueError(f"RVA outside sections: {rva:x}")

    def codeview(self) -> dict:
        rva, size = struct.unpack_from("<II", self.data, self.directories + 6 * 8)
        for i in range(size // 28):
            entry = self.offset(rva) + 28 * i
            kind = struct.unpack_from("<I", self.data, entry + 12)[0]
            length, _, pointer = struct.unpack_from("<III", self.data, entry + 16)
            value = self.data[pointer:pointer + length]
            if kind == 2 and value[:4] == b"RSDS":
                guid = uuid.UUID(bytes_le=value[4:20]).hex.upper()
                age = struct.unpack_from("<I", value, 20)[0]
                name = value[24:].split(b"\0", 1)[0].decode("utf-8")
                # PE embeds a PDB path; never allow it to choose our local directory.
                name = name.replace("\\", "/").rsplit("/", 1)[-1]
                if not name or name in (".", ".."):
                    raise ValueError("invalid PDB basename")
                identity = guid + format(age, "X")
                return {"name": name, "guid": guid, "age": age, "identity": identity,
                        "source": f"https://msdl.microsoft.com/download/symbols/{name}/{identity}/{name}"}
        raise ValueError("RSDS debug identity not found")


class SymbolInfo(ctypes.Structure):
    _fields_ = [
        ("SizeOfStruct", wintypes.ULONG), ("TypeIndex", wintypes.ULONG),
        ("Reserved", ctypes.c_ulonglong * 2), ("Index", wintypes.ULONG),
        ("Size", wintypes.ULONG), ("ModBase", ctypes.c_ulonglong),
        ("Flags", wintypes.ULONG), ("Value", ctypes.c_ulonglong),
        ("Address", ctypes.c_ulonglong), ("Register", wintypes.ULONG),
        ("Scope", wintypes.ULONG), ("Tag", wintypes.ULONG),
        ("NameLen", wintypes.ULONG), ("MaxNameLen", wintypes.ULONG),
        ("Name", wintypes.WCHAR * 1),
    ]


def enumerate_symbols(pe: PeFile, pdb_directory: Path, dbghelp_path: Path) -> list[dict]:
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.GetCurrentProcess.restype = wintypes.HANDLE
    process = kernel.GetCurrentProcess()
    dbghelp = ctypes.WinDLL(str(dbghelp_path), use_last_error=True)
    dbghelp.SymSetOptions.argtypes = [wintypes.DWORD]
    dbghelp.SymSetOptions.restype = wintypes.DWORD
    # Exact matching: never enable SYMOPT_LOAD_ANYTHING. Keep decorated names.
    # LOAD_LINES | EXACT_SYMBOLS | FAIL_CRITICAL_ERRORS | NO_PROMPTS.
    dbghelp.SymSetOptions(0x10 | 0x400 | 0x200 | 0x80000)
    dbghelp.SymInitializeW.argtypes = [wintypes.HANDLE, wintypes.LPCWSTR, wintypes.BOOL]
    dbghelp.SymInitializeW.restype = wintypes.BOOL
    dbghelp.SymCleanup.argtypes = [wintypes.HANDLE]
    dbghelp.SymCleanup.restype = wintypes.BOOL
    if not dbghelp.SymInitializeW(process, str(pdb_directory), False):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        dbghelp.SymLoadModuleExW.argtypes = [wintypes.HANDLE, wintypes.HANDLE, wintypes.LPCWSTR,
            wintypes.LPCWSTR, ctypes.c_ulonglong, wintypes.DWORD, ctypes.c_void_p, wintypes.DWORD]
        dbghelp.SymLoadModuleExW.restype = ctypes.c_ulonglong
        base = dbghelp.SymLoadModuleExW(process, None, str(pe.path), None, 0, 0, None, 0)
        if not base:
            raise ctypes.WinError(ctypes.get_last_error())
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, ctypes.POINTER(SymbolInfo),
                                           wintypes.ULONG, ctypes.c_void_p)
        found = []

        def callback(pointer, _size, _context):
            symbol = pointer.contents
            name = ctypes.wstring_at(ctypes.addressof(symbol) + SymbolInfo.Name.offset,
                                      symbol.NameLen).rstrip("\0")
            if any(name.startswith(prefix) for prefix in SYMBOL_FILTERS):
                rva = symbol.Address - base
                offset = pe.offset(rva)
                found.append({"name": name, "rva": rva, "rvaHex": hex(rva),
                              "first16Bytes": pe.data[offset:offset + 16].hex()})
            return True

        callback_ref = callback_type(callback)
        dbghelp.SymEnumSymbolsW.argtypes = [wintypes.HANDLE, ctypes.c_ulonglong,
                                          wintypes.LPCWSTR, callback_type, ctypes.c_void_p]
        dbghelp.SymEnumSymbolsW.restype = wintypes.BOOL
        if not dbghelp.SymEnumSymbolsW(process, base, None, callback_ref, None):
            raise ctypes.WinError(ctypes.get_last_error())
        if len(found) != len(SYMBOL_FILTERS):
            raise RuntimeError(f"expected {len(SYMBOL_FILTERS)} exact symbols, found {len(found)}")
        return found
    finally:
        dbghelp.SymCleanup(process)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--module", type=Path, default=Path(r"C:\Windows\System32\Taskbar.dll"))
    parser.add_argument("--output", type=Path, default=Path("out/taskbar-research"))
    parser.add_argument("--dbghelp", type=Path,
        default=Path(r"C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\dbghelp.dll"))
    parser.add_argument("--download-pdb", action="store_true",
        help="download the exact public PDB if not already present")
    args = parser.parse_args()
    if ctypes.sizeof(ctypes.c_void_p) != 8:
        raise RuntimeError("use x64 Python")
    pe = PeFile(args.module)
    codeview = pe.codeview()
    directory = args.output.resolve() / codeview["identity"]
    directory.mkdir(parents=True, exist_ok=True)
    pdb_path = directory / codeview["name"]
    if not pdb_path.exists():
        if not args.download_pdb:
            raise RuntimeError("exact PDB missing; use --download-pdb to fetch from Microsoft")
        with urllib.request.urlopen(codeview["source"], timeout=60) as response:
            pdb_path.write_bytes(response.read())
    symbols = enumerate_symbols(pe, directory, args.dbghelp.resolve())
    report = {"modulePath": str(pe.path), "machine": hex(pe.machine),
              "sha256": hashlib.sha256(pe.data).hexdigest(), "codeView": codeview,
              "symbols": symbols, "status": "symbol research only; no process injection or UI mutation"}
    output = args.output.resolve() / "symbol-evidence.json"
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
