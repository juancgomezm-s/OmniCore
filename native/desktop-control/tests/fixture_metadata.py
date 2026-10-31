"""Win32 metadata oracle for the test fixture; no production backend imports."""
import ctypes as C
from ctypes import wintypes as W
U=C.WinDLL('user32',use_last_error=True)
U.SetProcessDpiAwarenessContext.argtypes=[C.c_void_p]
U.SetProcessDpiAwarenessContext.restype=W.BOOL
U.SetProcessDpiAwarenessContext(C.c_void_p(-4))
U.GetWindowRect.argtypes=[W.HWND,C.POINTER(W.RECT)]
U.GetWindowRect.restype=W.BOOL
U.GetForegroundWindow.argtypes=[]
U.GetForegroundWindow.restype=W.HWND
def describe(handle):
    rect=W.RECT()
    if not U.GetWindowRect(handle,C.byref(rect)):raise OSError('Fixture window unavailable')
    return {'bounds':{'x':rect.left,'y':rect.top,'width':rect.right-rect.left,'height':rect.bottom-rect.top}}
