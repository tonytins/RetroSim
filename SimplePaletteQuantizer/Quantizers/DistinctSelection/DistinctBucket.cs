using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace SimplePaletteQuantizer.Quantizers.DistinctSelection
{
    public class DistinctBucket
    {
        public DistinctColorInfo ColorInfo { get; set; }
        public DistinctBucket[] Buckets { get; set; }

        public DistinctBucket()
        {
            Buckets = new DistinctBucket[16];
        }

        public void StoreColor(Color color)
        {
            int redIndex = color.R >> 5;
            var redBucket = Buckets[redIndex];

            if (redBucket == null)
            {
                redBucket = new DistinctBucket();
                Buckets[redIndex] = redBucket;
            }

            int greenIndex = color.G >> 5;
            var greenBucket = redBucket.Buckets[greenIndex];

            if (greenBucket == null)
            {
                greenBucket = new DistinctBucket();
                redBucket.Buckets[greenIndex] = greenBucket;
            }

            int blueIndex = color.B >> 5;
            var blueBucket = greenBucket.Buckets[blueIndex];

            if (blueBucket == null)
            {
                blueBucket = new DistinctBucket();
                greenBucket.Buckets[blueIndex] = blueBucket;
            }

            var colorInfo = blueBucket.ColorInfo;

            if (colorInfo == null)
            {
                colorInfo = new DistinctColorInfo(color);
                blueBucket.ColorInfo = colorInfo;
            }
            else
            {
                colorInfo.IncreaseCount();
            }
        }
        
        public List<DistinctColorInfo> GetValues()
        {
            return Buckets.Where(red => red != null).
                SelectMany(redBucket => redBucket.Buckets.
                Where(green => green != null), (redBucket, greenBucket) => greenBucket).
                SelectMany(greenBucket => greenBucket.Buckets.
                Where(blue => blue != null), (greenBucket, blueBucket) => blueBucket.ColorInfo).
                ToList();
        }
    }
}
