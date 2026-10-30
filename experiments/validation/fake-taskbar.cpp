#include <windows.h>

// A deliberately incompatible module for the owned-process fail-closed test.
// This is never passed to NativeBridge and is never loaded into Explorer.
BOOL WINAPI DllMain(HINSTANCE, DWORD, LPVOID) { return TRUE; }
