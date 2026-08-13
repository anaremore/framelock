param(
    [Parameter(Mandatory = $true)]
    [int]$TargetProcessId,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

Add-Type -AssemblyName System.Drawing.Common
Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class FrameLockCaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr windowHandle, out Rect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PrintWindow(IntPtr windowHandle, IntPtr deviceContext, uint flags);
}
'@

$targetProcess = Get-Process -Id $TargetProcessId -ErrorAction Stop
$targetProcess.Refresh()
if ($targetProcess.MainWindowHandle -eq [IntPtr]::Zero) {
    throw "Process $TargetProcessId does not have a main window."
}

$null = [FrameLockCaptureNative]::SetForegroundWindow($targetProcess.MainWindowHandle)
Start-Sleep -Milliseconds 350

$windowRectangle = New-Object FrameLockCaptureNative+Rect
if (-not [FrameLockCaptureNative]::GetWindowRect($targetProcess.MainWindowHandle, [ref]$windowRectangle)) {
    throw "Windows could not measure the target window."
}

$width = $windowRectangle.Right - $windowRectangle.Left
$height = $windowRectangle.Bottom - $windowRectangle.Top
if ($width -le 0 -or $height -le 0) {
    throw "The target window has an invalid size."
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
$outputDirectory = [System.IO.Path]::GetDirectoryName($resolvedOutput)
[System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null

$bitmap = New-Object System.Drawing.Bitmap($width, $height)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $deviceContext = $graphics.GetHdc()
    try {
        $rendered = [FrameLockCaptureNative]::PrintWindow(
            $targetProcess.MainWindowHandle,
            $deviceContext,
            2)
    }
    finally {
        $graphics.ReleaseHdc($deviceContext)
    }

    if (-not $rendered) {
        $graphics.CopyFromScreen(
            $windowRectangle.Left,
            $windowRectangle.Top,
            0,
            0,
            [System.Drawing.Size]::new($width, $height))
    }
    $bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $resolvedOutput
