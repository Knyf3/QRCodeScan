using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenCvSharp;
using SkiaSharp;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ZXing;

namespace QRCodeScan.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        private VideoCapture? _capture;
        private CancellationTokenSource? _cancellationTokenSource;
        private readonly ZXing.SkiaSharp.BarcodeReader _barcodeReader;
        private int _frameCounter = 0;

        [ObservableProperty]
        private WriteableBitmap? _cameraImage;

        [ObservableProperty]
        private string _scannedText = "No QR code detected";

        [ObservableProperty]
        private bool _isScanning;

        public MainWindowViewModel()
        {
            _barcodeReader = new ZXing.SkiaSharp.BarcodeReader
            {
                AutoRotate = false,
                Options = new ZXing.Common.DecodingOptions
                {
                    PossibleFormats = new[] { BarcodeFormat.QR_CODE },
                    TryHarder = false,
                    TryInverted = false
                }
            };
        }

        [RelayCommand]
        private async Task StartScanning()
        {
            if (IsScanning) return;

            IsScanning = true;
            ScannedText = "Scanning...";
            _cancellationTokenSource = new CancellationTokenSource();
            _frameCounter = 0;

            try
            {
                _capture = new VideoCapture(0, VideoCaptureAPIs.DSHOW);

                _capture.Set(VideoCaptureProperties.FrameWidth, 640);
                _capture.Set(VideoCaptureProperties.FrameHeight, 480);
                _capture.Set(VideoCaptureProperties.Fps, 30);

                if (!_capture.IsOpened())
                {
                    ScannedText = "Error: Cannot open camera";
                    IsScanning = false;
                    return;
                }

                // Give camera time to initialize
                await Task.Delay(500);

                await Task.Run(() => ProcessCamera(_cancellationTokenSource.Token));
            }
            catch (Exception ex)
            {
                ScannedText = $"Error: {ex.Message}";
                IsScanning = false;
            }
        }

        [RelayCommand]
        private void StopScanning()
        {
            _cancellationTokenSource?.Cancel();
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            IsScanning = false;
        }

        private void ProcessCamera(CancellationToken token)
        {
            using var frame = new Mat();

            while (!token.IsCancellationRequested && _capture?.IsOpened() == true)
            {
                try
                {
                    _capture.Read(frame);

                    if (frame.Empty() || frame.Width == 0 || frame.Height == 0)
                    {
                        Thread.Sleep(10);
                        continue;
                    }

                    _frameCounter++;

                    // Update camera image every 6 frames
                    if (_frameCounter % 6 == 0)
                    {
                        UpdateCameraImage(frame);
                    }

                    // Try to decode QR code
                    if (DecodeQRCode(frame))
                    {
                        break;
                    }

                    Thread.Sleep(30);
                }
                catch (Exception)
                {
                    // Continue on frame errors
                    Thread.Sleep(10);
                }
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                StopScanning();
            });
        }

        private void UpdateCameraImage(Mat frame)
        {
            if (frame.Empty() || frame.Width == 0 || frame.Height == 0)
                return;

            try
            {
                // Convert to grayscale for both display and faster processing
                using var gray = new Mat();
                Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);

                // Encode as JPEG (grayscale is much faster)
                Cv2.ImEncode(".jpg", gray, out var imageBytes, new int[] { (int)ImwriteFlags.JpegQuality, 70 });

                if (imageBytes != null && imageBytes.Length > 0)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        try
                        {
                            using var stream = new System.IO.MemoryStream(imageBytes);
                            CameraImage = WriteableBitmap.Decode(stream);
                        }
                        catch { /* Ignore UI update errors */ }
                    }, Avalonia.Threading.DispatcherPriority.Background);
                }
            }
            catch { /* Ignore display errors */ }
        }

        private bool DecodeQRCode(Mat frame)
        {
            if (frame.Empty() || frame.Width == 0 || frame.Height == 0)
                return false;

            try
            {
                // Resize for faster decoding
                using var resized = new Mat();
                Cv2.Resize(frame, resized, new OpenCvSharp.Size(320, 240), 0, 0, InterpolationFlags.Linear);

                if (resized.Empty())
                    return false;

                // Convert BGR to Grayscale for better QR detection
                using var gray = new Mat();
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);

                // Convert to SKBitmap
                using var skBitmap = MatToSKBitmap(gray);

                // Decode
                var result = _barcodeReader.Decode(skBitmap);

                if (result != null)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        ScannedText = $"QR Code: {result.Text}";
                    });
                    return true;
                }
            }
            catch { /* Ignore decode errors */ }

            return false;
        }

        private SKBitmap MatToSKBitmap(Mat mat)
        {
            if (mat.Empty())
                throw new ArgumentException("Mat is empty");

            // For grayscale images
            if (mat.Channels() == 1)
            {
                var info = new SKImageInfo(mat.Width, mat.Height, SKColorType.Gray8);
                var bitmap = new SKBitmap(info);

                // Get managed array from Mat
                byte[] data = new byte[mat.Total()];
                Marshal.Copy(mat.Data, data, 0, data.Length);

                // Copy to bitmap
                Marshal.Copy(data, 0, bitmap.GetPixels(), data.Length);
                return bitmap;
            }

            // For color images, convert to RGBA
            using var rgba = new Mat();
            Cv2.CvtColor(mat, rgba, ColorConversionCodes.BGR2RGBA);

            var rgbaInfo = new SKImageInfo(rgba.Width, rgba.Height, SKColorType.Rgba8888);
            var rgbaBitmap = new SKBitmap(rgbaInfo);

            // Get managed array
            int totalBytes = (int)(rgba.Total() * rgba.ElemSize());
            byte[] rgbaData = new byte[totalBytes];
            Marshal.Copy(rgba.Data, rgbaData, 0, totalBytes);

            // Copy to bitmap
            Marshal.Copy(rgbaData, 0, rgbaBitmap.GetPixels(), totalBytes);

            return rgbaBitmap;
        }
    }
}