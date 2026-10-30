param(
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$taskUi = Get-Process -Id $ProcessId
if ($taskUi.ProcessName -ne 'WorkFor.UI') {
    throw 'This capture helper only accepts a WorkFor.UI process.'
}
if ($taskUi.MainWindowHandle -eq [IntPtr]::Zero) { throw 'The UI has no main window.' }
Add-Type -AssemblyName System.Drawing
if (-not ('WorkForWindowCapture' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WorkForWindowCapture {
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out Rect bounds);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
}
$taskPreviousDpi = [WorkForWindowCapture]::SetThreadDpiAwarenessContext([IntPtr](-4))
$taskBounds = New-Object WorkForWindowCapture+Rect
if (-not [WorkForWindowCapture]::GetWindowRect($taskUi.MainWindowHandle, [ref]$taskBounds)) {
    throw 'GetWindowRect failed.'
}
$taskBitmap = New-Object System.Drawing.Bitmap ($taskBounds.Right - $taskBounds.Left), ($taskBounds.Bottom - $taskBounds.Top)
$taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
$taskHdc = $taskGraphics.GetHdc()
try {
    if (-not [WorkForWindowCapture]::PrintWindow($taskUi.MainWindowHandle, $taskHdc, 2)) {
        throw 'PrintWindow failed.'
    }
} finally {
    $taskGraphics.ReleaseHdc($taskHdc)
    $taskGraphics.Dispose()
}
try {
    $taskOutput = [System.IO.Path]::GetFullPath($OutputPath)
    [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
    $taskBitmap.Save($taskOutput, [System.Drawing.Imaging.ImageFormat]::Png)
    $taskOutput
} finally {
    $taskBitmap.Dispose()
    [WorkForWindowCapture]::SetThreadDpiAwarenessContext($taskPreviousDpi) | Out-Null
}
