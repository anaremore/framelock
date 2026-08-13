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

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        out Rect attributeValue,
        int attributeSize);
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

$cropLeft = 0
$cropTop = 0
$visibleWidth = $width
$visibleHeight = $height
$visibleRectangle = New-Object FrameLockCaptureNative+Rect
$extendedFrameBounds = 9
$dwmResult = [FrameLockCaptureNative]::DwmGetWindowAttribute(
    $targetProcess.MainWindowHandle,
    $extendedFrameBounds,
    [ref]$visibleRectangle,
    [System.Runtime.InteropServices.Marshal]::SizeOf($visibleRectangle))

if ($dwmResult -eq 0 -and
    $visibleRectangle.Left -ge $windowRectangle.Left -and
    $visibleRectangle.Top -ge $windowRectangle.Top -and
    $visibleRectangle.Right -le $windowRectangle.Right -and
    $visibleRectangle.Bottom -le $windowRectangle.Bottom -and
    $visibleRectangle.Right -gt $visibleRectangle.Left -and
    $visibleRectangle.Bottom -gt $visibleRectangle.Top) {
    $cropLeft = $visibleRectangle.Left - $windowRectangle.Left
    $cropTop = $visibleRectangle.Top - $windowRectangle.Top
    $visibleWidth = $visibleRectangle.Right - $visibleRectangle.Left
    $visibleHeight = $visibleRectangle.Bottom - $visibleRectangle.Top
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
    if ($cropLeft -eq 0 -and
        $cropTop -eq 0 -and
        $visibleWidth -eq $width -and
        $visibleHeight -eq $height) {
        $bitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    else {
        $visibleBitmap = New-Object System.Drawing.Bitmap($visibleWidth, $visibleHeight)
        $visibleGraphics = [System.Drawing.Graphics]::FromImage($visibleBitmap)
        try {
            $destination = [System.Drawing.Rectangle]::new(0, 0, $visibleWidth, $visibleHeight)
            $source = [System.Drawing.Rectangle]::new($cropLeft, $cropTop, $visibleWidth, $visibleHeight)
            $visibleGraphics.DrawImage(
                $bitmap,
                $destination,
                $source,
                [System.Drawing.GraphicsUnit]::Pixel)
            $visibleBitmap.Save($resolvedOutput, [System.Drawing.Imaging.ImageFormat]::Png)
        }
        finally {
            $visibleGraphics.Dispose()
            $visibleBitmap.Dispose()
        }
    }
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output $resolvedOutput
