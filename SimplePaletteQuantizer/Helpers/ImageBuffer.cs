using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SimplePaletteQuantizer.ColorCaches.Common;
using SimplePaletteQuantizer.Ditherers;
using SimplePaletteQuantizer.Extensions;
using SimplePaletteQuantizer.PathProviders;
using SimplePaletteQuantizer.Quantizers;

namespace SimplePaletteQuantizer.Helpers
{
    public class ImageBuffer : IDisposable
    {
        #region | Fields |

        int[] fastBitX;
        int[] fastByteX;
        int[] fastY;

        readonly Bitmap bitmap;
        readonly BitmapData bitmapData;
        readonly ImageLockMode lockMode;

        List<Color> cachedPalette;

        #endregion

        #region | Delegates |

        public delegate bool ProcessPixelFunction(Pixel pixel);
        public delegate bool ProcessPixelAdvancedFunction(Pixel pixel, ImageBuffer buffer);
        public delegate bool TransformPixelFunction(Pixel sourcePixel, Pixel targetPixel);
        public delegate bool TransformPixelAdvancedFunction(Pixel sourcePixel, Pixel targetPixel, ImageBuffer sourceBuffer, ImageBuffer targetBuffer);

        #endregion

        #region | Properties |

        public int Width { get; set; }
        public int Height { get; set; }

        public int Size { get; set; }
        public int Stride { get; set; }
        public int BitDepth { get; set; }
        public int BytesPerPixel { get; set; }

        public bool IsIndexed { get; set; }
        public PixelFormat PixelFormat { get; set; }

        #endregion

        #region | Calculated properties |

        /// <summary>
        /// Gets a value indicating whether this buffer can be read.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance can read; otherwise, <c>false</c>.
        /// </value>
        public bool CanRead
        {
            get { return lockMode == ImageLockMode.ReadOnly || lockMode == ImageLockMode.ReadWrite; }
        }

        /// <summary>
        /// Gets a value indicating whether this buffer can written to.
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance can write; otherwise, <c>false</c>.
        /// </value>
        public bool CanWrite
        {
            get { return lockMode == ImageLockMode.WriteOnly || lockMode == ImageLockMode.ReadWrite; }
        }

        /// <summary>
        /// Gets or sets the palette.
        /// </summary>
        public List<Color> Palette
        {
            get { return UpdatePalette(); }
            set
            {
                bitmap.SetPalette(value);
                cachedPalette = value;
            }
        }

        #endregion

        #region | Constructors |

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageBuffer"/> class.
        /// </summary>
        public ImageBuffer(Image bitmap, ImageLockMode lockMode) : this((Bitmap)bitmap, lockMode) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="ImageBuffer"/> class.
        /// </summary>
        public ImageBuffer(Bitmap bitmap, ImageLockMode lockMode)
        {
            // locks the image data
            this.bitmap = bitmap;
            this.lockMode = lockMode;

            // gathers the informations
            Width = bitmap.Width;
            Height = bitmap.Height;
            PixelFormat = bitmap.PixelFormat;
            IsIndexed = PixelFormat.IsIndexed();
            BitDepth = PixelFormat.GetBitDepth();
            BytesPerPixel = Math.Max(1, BitDepth >> 3);

            // determines the bounds of an image, and locks the data in a specified mode
            Rectangle bounds = Rectangle.FromLTRB(0, 0, Width, Height);

            // locks the bitmap data
            lock (bitmap)
                bitmapData = bitmap.LockBits(bounds, lockMode, PixelFormat);

            // creates internal buffer
            Stride = bitmapData.Stride < 0 ? -bitmapData.Stride : bitmapData.Stride;
            Size = Stride * Height;

            // precalculates the offsets
            Precalculate();
        }

        #endregion

        #region | Maintenance methods |

        void Precalculate()
        {
            fastBitX = new int[Width];
            fastByteX = new int[Width];
            fastY = new int[Height];

            // precalculates the x-coordinates
            for (int x = 0; x < Width; x++)
            {
                fastBitX[x] = x * BitDepth;
                fastByteX[x] = fastBitX[x] >> 3;
                fastBitX[x] = fastBitX[x] % 8;
            }

            // precalculates the y-coordinates
            for (int y = 0; y < Height; y++)
            {
                fastY[y] = y * bitmapData.Stride;
            }
        }

        public int GetBitOffset(int x)
        {
            return fastBitX[x];
        }

        public byte[] Copy()
        {
            // transfers whole image to a working memory
            byte[] result = new byte[Size];
            Marshal.Copy(bitmapData.Scan0, result, 0, Size);

            // returns the backup
            return result;
        }

        public void Paste(byte[] buffer)
        {
            // commits the data to a bitmap
            Marshal.Copy(buffer, 0, bitmapData.Scan0, Size);
        }

        #endregion

        #region | Pixel read methods |

        public void ReadPixel(Pixel pixel, byte[] buffer = null)
        {
            // determines pixel offset at [x, y]
            int offset = fastByteX[pixel.X] + fastY[pixel.Y];

            // reads the pixel from a bitmap
            if (buffer == null)
            {
                pixel.ReadRawData(bitmapData.Scan0 + offset);
            }
            else // reads the pixel from a buffer
            {
                pixel.ReadData(buffer, offset);
            }
        }

        public int GetIndexFromPixel(Pixel pixel)
        {
            int result;

            // determines whether the format is indexed
            if (IsIndexed)
            {
                result = pixel.Index;
            }
            else // not possible to get index from a non-indexed format
            {
                string message = string.Format("Cannot retrieve index for a non-indexed format. Please use Color (or Value) property instead.");
                throw new NotSupportedException(message);
            }

            return result;
        }

        public Color GetColorFromPixel(Pixel pixel)
        {
            Color result;

            // determines whether the format is indexed
            if (pixel.IsIndexed)
            {
                int index = pixel.Index;
                result = pixel.Parent.GetPaletteColor(index);
            }
            else // gets color from a non-indexed format
            {
                result = pixel.Color;
            }

            // returns the found color
            return result;
        }

        public int ReadIndexUsingPixel(Pixel pixel, byte[] buffer = null)
        {
            // reads the pixel from bitmap/buffer
            ReadPixel(pixel, buffer);

            // returns the found color
            return GetIndexFromPixel(pixel);
        }

        public Color ReadColorUsingPixel(Pixel pixel, byte[] buffer = null)
        {
            // reads the pixel from bitmap/buffer
            ReadPixel(pixel, buffer);

            // returns the found color
            return GetColorFromPixel(pixel);
        }

        public int ReadIndexUsingPixelFrom(Pixel pixel, int x, int y, byte[] buffer = null)
        {
            // redirects pixel -> [x, y]
            pixel.Update(x, y);

            // reads index from a bitmap/buffer using pixel, and stores it in the pixel
            return ReadIndexUsingPixel(pixel, buffer);
        }

        public Color ReadColorUsingPixelFrom(Pixel pixel, int x, int y, byte[] buffer = null)
        {
            // redirects pixel -> [x, y]
            pixel.Update(x, y);

            // reads color from a bitmap/buffer using pixel, and stores it in the pixel
            return ReadColorUsingPixel(pixel, buffer);
        }

        #endregion

        #region | Pixel write methods |

        void WritePixel(Pixel pixel, byte[] buffer = null)
        {
            // determines pixel offset at [x, y]
            int offset = fastByteX[pixel.X] + fastY[pixel.Y];

            // writes the pixel to a bitmap
            if (buffer == null)
            {
                pixel.WriteRawData(bitmapData.Scan0 + offset);
            }
            else // writes the pixel to a buffer
            {
                pixel.WriteData(buffer, offset);
            }
        }

        public void SetIndexToPixel(Pixel pixel, int index, byte[] buffer = null)
        {
            // determines whether the format is indexed
            if (IsIndexed)
            {
                pixel.Index = (byte)index;
            }
            else // cannot write color to an indexed format
            {
                string message = string.Format("Cannot set index for a non-indexed format. Please use Color (or Value) property instead.");
                throw new NotSupportedException(message);
            }
        }

        public void SetColorToPixel(Pixel pixel, Color color, IColorQuantizer quantizer)
        {
            // determines whether the format is indexed
            if (pixel.IsIndexed)
            {
                // last chance if quantizer is provided, use it
                if (quantizer != null)
                {
                    byte index = (byte)quantizer.GetPaletteIndex(color, pixel.X, pixel.Y);
                    pixel.Index = index;
                }
                else // cannot write color to an index format
                {
                    string message = string.Format("Cannot retrieve color for an indexed format. Use GetPixelIndex() instead.");
                    throw new NotSupportedException(message);
                }
            }
            else // sets color to a non-indexed format
            {
                pixel.Color = color;
            }
        }

        public void WriteIndexUsingPixel(Pixel pixel, int index, byte[] buffer = null)
        {
            // sets index to pixel (pixel's index is updated)
            SetIndexToPixel(pixel, index, buffer);

            // writes pixel to a bitmap/buffer
            WritePixel(pixel, buffer);
        }

        public void WriteColorUsingPixel(Pixel pixel, Color color, IColorQuantizer quantizer, byte[] buffer = null)
        {
            // sets color to pixel (pixel is updated with color)
            SetColorToPixel(pixel, color, quantizer);

            // writes pixel to a bitmap/buffer
            WritePixel(pixel, buffer);
        }

        public void WriteIndexUsingPixelAt(Pixel pixel, int x, int y, int index, byte[] buffer = null)
        {
            // redirects pixel -> [x, y]
            pixel.Update(x, y);

            // writes color to bitmap/buffer using pixel
            WriteIndexUsingPixel(pixel, index, buffer);
        }

        public void WriteColorUsingPixelAt(Pixel pixel, int x, int y, Color color, IColorQuantizer quantizer, byte[] buffer = null)
        {
            // redirects pixel -> [x, y]
            pixel.Update(x, y);

            // writes color to bitmap/buffer using pixel
            WriteColorUsingPixel(pixel, color, quantizer, buffer);
        }

        #endregion

        #region | Generic methods |

        void ProcessInParallel(ICollection<Point> path, Action<LineTask> process, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(process, "process");

            // updates the palette
            UpdatePalette();

            // prepares parallel processing
            double pointsPerTask = (1.0 * path.Count) / parallelTaskCount;
            LineTask[] lineTasks = new LineTask[parallelTaskCount];
            double pointOffset = 0.0;

            // creates task for each batch of rows
            for (int index = 0; index < parallelTaskCount; index++)
            {
                lineTasks[index] = new LineTask((int)pointOffset, (int)(pointOffset + pointsPerTask));
                pointOffset += pointsPerTask;
            }

            // process the image in a parallel manner
            Parallel.ForEach(lineTasks, process);
        }

        #endregion

        #region | Processing methods |

        void ProcessPerPixelBase(IList<Point> path, Delegate processingAction, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(path, "path");
            Guard.CheckNull(processingAction, "processPixelFunction");

            // determines mode
            bool isAdvanced = processingAction is ProcessPixelAdvancedFunction;

            // prepares the per pixel task
            void processPerPixel(LineTask lineTask)
            {
                // initializes variables per task
                Pixel pixel = new Pixel(this);

                for (int pathOffset = lineTask.StartOffset; pathOffset < lineTask.EndOffset; pathOffset++)
                {
                    var point = path[pathOffset];
                    bool allowWrite;

                    // enumerates the pixel, and returns the control to the outside
                    pixel.Update(point.X, point.Y);

                    // when read is allowed, retrieves current value (in bytes)
                    if (CanRead)
                        ReadPixel(pixel);

                    // process the pixel by custom user operation
                    if (isAdvanced)
                    {
                        ProcessPixelAdvancedFunction processAdvancedFunction = (ProcessPixelAdvancedFunction)processingAction;
                        allowWrite = processAdvancedFunction(pixel, this);
                    }
                    else // use simplified version with pixel parameter only
                    {
                        ProcessPixelFunction processFunction = (ProcessPixelFunction)processingAction;
                        allowWrite = processFunction(pixel);
                    }

                    // when write is allowed, copies the value back to the row buffer
                    if (CanWrite && allowWrite)
                        WritePixel(pixel);
                }
            }

            // processes image per pixel
            ProcessInParallel(path, processPerPixel, parallelTaskCount);
        }

        public void ProcessPerPixel(IList<Point> path, ProcessPixelFunction processPixelFunction, int parallelTaskCount = 4)
        {
            ProcessPerPixelBase(path, processPixelFunction, parallelTaskCount);
        }

        public void ProcessPerPixelAdvanced(IList<Point> path, ProcessPixelAdvancedFunction processPixelAdvancedFunction, int parallelTaskCount = 4)
        {
            ProcessPerPixelBase(path, processPixelAdvancedFunction, parallelTaskCount);
        }

        #endregion

        #region | Transformation functions |

        void TransformPerPixelBase(ImageBuffer target, IList<Point> path, Delegate transformAction, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(path, "path");
            Guard.CheckNull(target, "target");
            Guard.CheckNull(transformAction, "transformAction");

            // updates the palette
            UpdatePalette();
            target.UpdatePalette();

            // checks the dimensions
            if (Width != target.Width || Height != target.Height)
            {
                const string message = "Both images have to have the same dimensions.";
                throw new ArgumentOutOfRangeException(message);
            }

            // determines mode
            bool isAdvanced = transformAction is TransformPixelAdvancedFunction;

            // process the image in a parallel manner
            void transformPerPixel(LineTask lineTask)
            {
                // creates individual pixel structures per task
                Pixel sourcePixel = new Pixel(this);
                Pixel targetPixel = new Pixel(target);

                // enumerates the pixels row by row
                for (int pathOffset = lineTask.StartOffset; pathOffset < lineTask.EndOffset; pathOffset++)
                {
                    var point = path[pathOffset];
                    bool allowWrite;

                    // enumerates the pixel, and returns the control to the outside
                    sourcePixel.Update(point.X, point.Y);
                    targetPixel.Update(point.X, point.Y);

                    // when read is allowed, retrieves current value (in bytes)
                    if (CanRead)
                        ReadPixel(sourcePixel);
                    if (target.CanRead)
                        target.ReadPixel(targetPixel);

                    // process the pixel by custom user operation
                    if (isAdvanced)
                    {
                        TransformPixelAdvancedFunction transformAdvancedFunction = (TransformPixelAdvancedFunction)transformAction;
                        allowWrite = transformAdvancedFunction(sourcePixel, targetPixel, this, target);
                    }
                    else // use simplified version with pixel parameters only
                    {
                        TransformPixelFunction transformFunction = (TransformPixelFunction)transformAction;
                        allowWrite = transformFunction(sourcePixel, targetPixel);
                    }

                    // when write is allowed, copies the value back to the row buffer
                    if (target.CanWrite && allowWrite)
                        target.WritePixel(targetPixel);
                }
            }

            // transforms image per pixel
            ProcessInParallel(path, transformPerPixel, parallelTaskCount);
        }

        public void TransformPerPixel(ImageBuffer target, IList<Point> path, TransformPixelFunction transformPixelFunction, int parallelTaskCount = 4)
        {
            TransformPerPixelBase(target, path, transformPixelFunction, parallelTaskCount);
        }

        public void TransformPerPixelAdvanced(ImageBuffer target, IList<Point> path, TransformPixelAdvancedFunction transformPixelAdvancedFunction, int parallelTaskCount = 4)
        {
            TransformPerPixelBase(target, path, transformPixelAdvancedFunction, parallelTaskCount);
        }

        #endregion

        #region | Scan colors methods |

        public void ScanColors(IColorQuantizer quantizer, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(quantizer, "quantizer");

            // determines which method of color retrieval to use
            var path = quantizer.GetPointPath(Width, Height);

            // use different scanning method depending whether the image format is indexed
            bool scanColors(Pixel pixel)
            {
                quantizer.AddColor(GetColorFromPixel(pixel), pixel.X, pixel.Y);
                return false;
            }

            // performs the image scan, using a chosen method
            ProcessPerPixel(path, scanColors, parallelTaskCount);
        }

        public static void ScanImageColors(Image sourceImage, IColorQuantizer quantizer, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                source.ScanColors(quantizer, parallelTaskCount);
            }
        }

        #endregion

        #region | Synthetize palette methods |

        public List<Color> SynthetizePalette(IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(quantizer, "quantizer");

            // Step 1 - prepares quantizer for another round
            quantizer.Prepare(this);

            // Step 2 - scans the source image for the colors
            ScanColors(quantizer, parallelTaskCount);

            // Step 3 - synthetises the palette, and returns the result
            return quantizer.GetPalette(colorCount);
        }

        public static List<Color> SynthetizeImagePalette(Image sourceImage, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                return source.SynthetizePalette(quantizer, colorCount, parallelTaskCount);
            }
        }

        #endregion

        #region | Quantize methods |

        public void Quantize(ImageBuffer target, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // performs the pure quantization wihout dithering
            Quantize(target, quantizer, null, colorCount, parallelTaskCount);
        }

        public void Quantize(ImageBuffer target, IColorQuantizer quantizer, IColorDitherer ditherer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(target, "target");
            Guard.CheckNull(quantizer, "quantizer");

            // initializes quantization parameters
            bool isTargetIndexed = target.PixelFormat.IsIndexed();

            // step 1 - prepares the palettes
            var targetPalette = isTargetIndexed ? SynthetizePalette(quantizer, colorCount, parallelTaskCount) : null;

            // step 2 - updates the bitmap palette
            target.bitmap.SetPalette(targetPalette);
            target.UpdatePalette(true);

            // step 3 - prepares ditherer (optional)
            if (ditherer != null)
                ditherer.Prepare(quantizer, colorCount, this, target);

            // step 4 - prepares the quantization function
            bool quantize(Pixel sourcePixel, Pixel targetPixel)
            {
                // reads the pixel color
                var color = GetColorFromPixel(sourcePixel);

                // converts alpha to solid color
                color = QuantizationHelper.ConvertAlpha(color);

                // quantizes the pixel
                SetColorToPixel(targetPixel, color, quantizer);

                // marks pixel as processed by default
                bool result = true;

                // preforms inplace dithering (optional)
                if (ditherer != null && ditherer.IsInplace)
                {
                    result = ditherer.ProcessPixel(sourcePixel, targetPixel);
                }

                // returns the result
                return result;
            }

            // step 5 - generates the target image
            var path = quantizer.GetPointPath(Width, Height);
            TransformPerPixel(target, path, quantize, parallelTaskCount);

            // step 6 - preforms non-inplace dithering (optional)
            if (ditherer != null && !ditherer.IsInplace)
            {
                Dither(target, ditherer, quantizer, colorCount, 1);
            }

            // step 7 - finishes the dithering (optional)
            if (ditherer != null)
                ditherer.Finish();

            // step 8 - clean-up
            quantizer.Finish();
        }

        public static Image QuantizeImage(ImageBuffer source, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // performs the pure quantization wihout dithering
            return QuantizeImage(source, quantizer, null, colorCount, parallelTaskCount);
        }

        public static Image QuantizeImage(ImageBuffer source, IColorQuantizer quantizer, IColorDitherer ditherer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");

            // creates a target bitmap in an appropriate format
            var targetPixelFormat = Extend.GetFormatByColorCount(colorCount);
            Image result = new Bitmap(source.Width, source.Height, targetPixelFormat);

            // lock mode
            var lockMode = ditherer == null ? ImageLockMode.WriteOnly : ImageLockMode.ReadWrite;

            // wraps target image to a buffer
            using (ImageBuffer target = new ImageBuffer(result, lockMode))
            {
                source.Quantize(target, quantizer, ditherer, colorCount, parallelTaskCount);
                return result;
            }
        }

        public static Image QuantizeImage(Image sourceImage, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // performs the pure quantization wihout dithering
            return QuantizeImage(sourceImage, quantizer, null, colorCount, parallelTaskCount);
        }

        public static Image QuantizeImage(Image sourceImage, IColorQuantizer quantizer, IColorDitherer ditherer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // lock mode
            var lockMode = ditherer == null ? ImageLockMode.ReadOnly : ImageLockMode.ReadWrite;

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, lockMode))
            {
                return QuantizeImage(source, quantizer, ditherer, colorCount, parallelTaskCount);
            }
        }

        #endregion

        #region | Calculate mean error methods |

        public double CalculateMeanError(ImageBuffer target, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(target, "target");

            // initializes the error
            long totalError = 0;

            // prepares the function
            bool calculateMeanError(Pixel sourcePixel, Pixel targetPixel)
            {
                var sourceColor = GetColorFromPixel(sourcePixel);
                var targetColor = GetColorFromPixel(targetPixel);
                totalError += ColorModelHelper.GetColorEuclideanDistance(ColorModel.RedGreenBlue, sourceColor, targetColor);
                return false;
            }

            // performs the image scan, using a chosen method
            var standardPath = new StandardPathProvider().GetPointPath(Width, Height);
            TransformPerPixel(target, standardPath, calculateMeanError, parallelTaskCount);

            // returns the calculates RMSD
            return Math.Sqrt(totalError / (3.0 * Width * Height));
        }

        public static double CalculateImageMeanError(ImageBuffer source, ImageBuffer target, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");

            // use other override to calculate error
            return source.CalculateMeanError(target, parallelTaskCount);
        }

        public static double CalculateImageMeanError(ImageBuffer source, Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateMeanError(target, parallelTaskCount);
            }
        }

        public static double CalculateImageMeanError(Image sourceImage, ImageBuffer target, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateMeanError(target, parallelTaskCount);
            }
        }

        public static double CalculateImageMeanError(Image sourceImage, Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateMeanError(target, parallelTaskCount);
            }
        }

        #endregion

        #region | Calculate normalized mean error methods |

        public double CalculateNormalizedMeanError(ImageBuffer target, int parallelTaskCount = 4)
        {
            return CalculateMeanError(target, parallelTaskCount) / 255.0;
        }

        public static double CalculateImageNormalizedMeanError(ImageBuffer source, Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateNormalizedMeanError(target, parallelTaskCount);
            }
        }

        public static double CalculateImageNormalizedMeanError(Image sourceImage, ImageBuffer target, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateNormalizedMeanError(target, parallelTaskCount);
            }
        }

        public static double CalculateImageNormalizedMeanError(ImageBuffer source, ImageBuffer target, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");

            // use other override to calculate error
            return source.CalculateNormalizedMeanError(target, parallelTaskCount);
        }

        public static double CalculateImageNormalizedMeanError(Image sourceImage, Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                return source.CalculateNormalizedMeanError(target, parallelTaskCount);
            }
        }

        #endregion

        #region | Change pixel format methods |

        public void ChangeFormat(ImageBuffer target, IColorQuantizer quantizer, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(target, "target");
            Guard.CheckNull(quantizer, "quantizer");

            // gathers some information about the target format
            bool hasSourceAlpha = PixelFormat.HasAlpha();
            bool hasTargetAlpha = target.PixelFormat.HasAlpha();
            bool isTargetIndexed = target.PixelFormat.IsIndexed();
            bool isSourceDeepColor = PixelFormat.IsDeepColor();
            bool isTargetDeepColor = target.PixelFormat.IsDeepColor();

            // step 1 to 3 - prepares the palettes
            if (isTargetIndexed)
                SynthetizePalette(quantizer, target.PixelFormat.GetColorCount(), parallelTaskCount);

            // prepares the quantization function
            bool changeFormat(Pixel sourcePixel, Pixel targetPixel)
            {
                // if both source and target formats are deep color formats, copies a value directly
                if (isSourceDeepColor && isTargetDeepColor)
                {
                    //UInt64 value = sourcePixel.Value;
                    //targetPixel.SetValue(value);
                }
                else
                {
                    // retrieves a source image color
                    var color = GetColorFromPixel(sourcePixel);

                    // if alpha is not present in the source image, but is present in the target, make one up
                    if (!hasSourceAlpha && hasTargetAlpha)
                    {
                        int argb = 255 << 24 | color.R << 16 | color.G << 8 | color.B;
                        color = Color.FromArgb(argb);
                    }

                    // sets the color to a target pixel
                    SetColorToPixel(targetPixel, color, quantizer);
                }

                // allows to write (obviously) the transformed pixel
                return true;
            }

            // step 5 - generates the target image
            var standardPath = new StandardPathProvider().GetPointPath(Width, Height);
            TransformPerPixel(target, standardPath, changeFormat, parallelTaskCount);
        }

        public static void ChangeFormat(ImageBuffer source, PixelFormat targetFormat, IColorQuantizer quantizer, out Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");

            // creates a target bitmap in an appropriate format
            targetImage = new Bitmap(source.Width, source.Height, targetFormat);

            // wraps target image to a buffer
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.WriteOnly))
            {
                source.ChangeFormat(target, quantizer, parallelTaskCount);
            }
        }

        public static void ChangeFormat(Image sourceImage, PixelFormat targetFormat, IColorQuantizer quantizer, out Image targetImage, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                ChangeFormat(source, targetFormat, quantizer, out targetImage, parallelTaskCount);
            }
        }

        #endregion

        #region | Dithering methods |

        public void Dither(ImageBuffer target, IColorDitherer ditherer, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(target, "target");
            Guard.CheckNull(ditherer, "ditherer");
            Guard.CheckNull(quantizer, "quantizer");

            // prepares ditherer for another round
            ditherer.Prepare(quantizer, colorCount, this, target);

            // processes the image via the ditherer
            var path = ditherer.GetPointPath(Width, Height);
            TransformPerPixel(target, path, ditherer.ProcessPixel, parallelTaskCount);
        }

        public static void DitherImage(ImageBuffer source, ImageBuffer target, IColorDitherer ditherer, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");

            // use other override to calculate error
            source.Dither(target, ditherer, quantizer, colorCount, parallelTaskCount);
        }

        public static void DitherImage(ImageBuffer source, Image targetImage, IColorDitherer ditherer, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(source, "source");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                source.Dither(target, ditherer, quantizer, colorCount, parallelTaskCount);
            }
        }

        public static void DitherImage(Image sourceImage, ImageBuffer target, IColorDitherer ditherer, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                source.Dither(target, ditherer, quantizer, colorCount, parallelTaskCount);
            }
        }

        public static void DitherImage(Image sourceImage, Image targetImage, IColorDitherer ditherer, IColorQuantizer quantizer, int colorCount, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");
            Guard.CheckNull(targetImage, "targetImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            using (ImageBuffer target = new ImageBuffer(targetImage, ImageLockMode.ReadOnly))
            {
                // use other override to calculate error
                source.Dither(target, ditherer, quantizer, colorCount, parallelTaskCount);
            }
        }

        #endregion

        #region | Gamma correction |

        public void CorrectGamma(float gamma, IColorQuantizer quantizer, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(quantizer, "quantizer");

            // determines which method of color retrieval to use
            var path = quantizer.GetPointPath(Width, Height);

            // calculates gamma ramp
            int[] gammaRamp = new int[256];

            for (int index = 0; index < 256; ++index)
            {
                gammaRamp[index] = Clamp((int)((255.0f * Math.Pow(index / 255.0f, 1.0f / gamma)) + 0.5f));
            }

            // use different scanning method depending whether the image format is indexed
            bool correctGamma(Pixel pixel)
            {
                var oldColor = GetColorFromPixel(pixel);
                int red = gammaRamp[oldColor.R];
                int green = gammaRamp[oldColor.G];
                int blue = gammaRamp[oldColor.B];
                Color newColor = Color.FromArgb(red, green, blue);
                SetColorToPixel(pixel, newColor, quantizer);
                return true;
            }

            // performs the image scan, using a chosen method
            ProcessPerPixel(path, correctGamma, parallelTaskCount);
        }

        public static void CorrectImageGamma(Image sourceImage, float gamma, IColorQuantizer quantizer, int parallelTaskCount = 4)
        {
            // checks parameters
            Guard.CheckNull(sourceImage, "sourceImage");

            // wraps source image to a buffer
            using (ImageBuffer source = new ImageBuffer(sourceImage, ImageLockMode.ReadOnly))
            {
                source.CorrectGamma(gamma, quantizer, parallelTaskCount);
            }
        }

        #endregion

        #region | Palette methods |

        public static int Clamp(int value, int minimum = 0, int maximum = 255)
        {
            if (value < minimum)
                value = minimum;
            if (value > maximum)
                value = maximum;
            return value;
        }

        List<Color> UpdatePalette(bool forceUpdate = false)
        {
            if (IsIndexed && (cachedPalette == null || forceUpdate))
            {
                cachedPalette = bitmap.GetPalette();
            }

            return cachedPalette;
        }

        public Color GetPaletteColor(int paletteIndex)
        {
            return cachedPalette[paletteIndex];
        }

        #endregion

        #region << IDisposable >>

        public void Dispose()
        {
            // releases the image lock
            lock (bitmap)
                bitmap.UnlockBits(bitmapData);
        }

        #endregion

        #region | Sub-classes |

        class LineTask
        {
            /// <summary>
            /// Gets or sets the start offset.
            /// </summary>
            public int StartOffset { get; set; }

            /// <summary>
            /// Gets or sets the end offset.
            /// </summary>
            public int EndOffset { get; set; }

            /// <summary>
            /// Initializes a new instance of the <see cref="SimplePaletteQuantizer.Helpers.ImageBuffer.LineTask"/> class.
            /// </summary>
            public LineTask(int startOffset, int endOffset)
            {
                StartOffset = startOffset;
                EndOffset = endOffset;
            }
        }

        #endregion
    }
}
