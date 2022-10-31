using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Emgu.CV;
using Emgu.CV.CvEnum;
using Jellyfin.Plugin.JavScraper.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.JavScraper.Services
{
    /// <summary>
    /// 图片代理服务
    /// </summary>
    public sealed class ImageProxyService : IDisposable
    {
        private readonly IServerApplicationHost _serverApplicationHost;
        private readonly ILogger _logger;
        private readonly IFileSystem _fileSystem;
        private readonly IApplicationPaths _appPaths;
        private readonly IHttpClientFactory _clientFactory;
        private readonly CascadeClassifier _cascadeClassifier = new("haarcascade_frontalface_default.xml");

        public ImageProxyService(
            IServerApplicationHost serverApplicationHost,
            ILoggerFactory loggerFactory,
            IFileSystem fileSystem,
            IApplicationPaths appPaths,
            IHttpClientFactory clientFactory)
        {
            _serverApplicationHost = serverApplicationHost;
            _logger = loggerFactory.CreateLogger<ImageProxyService>();
            _fileSystem = fileSystem;
            _appPaths = appPaths;
            _clientFactory = clientFactory;
        }

        /// <summary>
        /// 构造本地url地址
        /// </summary>
        /// <param name="url"></param>
        /// <param name="type"></param>
        /// <param name="withApiUrl">是否包含 api url</param>
        /// <returns></returns>
        public string GetLocalUrl(string url, ImageType type = ImageType.Backdrop, bool withApiUrl = true)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (url.Contains("Plugins/JavScraper/Image", StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }

            var apiUrl = withApiUrl ? _serverApplicationHost.GetApiUrlForLocalAccess() : string.Empty;
            return $"{apiUrl}/Jellyfin/Plugins/JavScraper/Image?url={HttpUtility.UrlEncode(url)}&type={type}";
        }

        /// <summary>
        /// 获取图片
        /// </summary>
        /// <param name="uriString">地址</param>
        /// <param name="type">类型</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<HttpResponseMessage> GetImageResponse(string uriString, ImageType type, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(uriString))
            {
                throw new ArgumentException($"{nameof(uriString)} can not be null or space", nameof(uriString));
            }

            // /Jellyfin/Plugins/JavScraper/Image?url=&type=xx
            if (uriString.Contains("Plugins/JavScraper/Image", StringComparison.OrdinalIgnoreCase)) // 本地的链接
            {
                var uri = new Uri(uriString);
                var queryParameters = HttpUtility.ParseQueryString(uri.Query);
                var urlFromQuery = queryParameters["url"];
                if (urlFromQuery.IsWebUrl())
                {
                    uriString = urlFromQuery;
                    if (Enum.TryParse<ImageType>(queryParameters["type"]?.Trim(), out var typeFromQuery))
                    {
                        type = typeFromQuery;
                    }
                }
            }

            _logger.LogInformation("{Method}-{Uri}-{Type}", nameof(GetImageResponse), uriString, type);

            var key = WebUtility.UrlEncode(uriString);
            var cacheDirectory = _appPaths.ImageCachePath;
            Directory.CreateDirectory(cacheDirectory);

            var cacheFilePath = Path.Combine(cacheDirectory, key);

            // 尝试从缓存中读取
            byte[] imageByteArray;
            try
            {
                if (cacheFilePath.Contains("../", StringComparison.Ordinal) || cacheFilePath.Length > 256)
                {
                    throw new ArgumentException(nameof(key));
                }
#pragma warning disable CA3003
                var cacheFile = _fileSystem.GetFileInfo(cacheFilePath);
#pragma warning disable CA3003
                // 图片文件存在，且是24小时之内的
                if (cacheFile.Exists && cacheFile.LastWriteTimeUtc > DateTime.Now.AddDays(-1).ToUniversalTime())
                {
                    _logger.LogInformation("Hit image cache {Uri} {File}", $"{nameof(uriString)}={uriString}", $"{nameof(cacheFilePath)}={cacheFilePath}");
                    imageByteArray = await File.ReadAllBytesAsync(cacheFilePath, CancellationToken.None).ConfigureAwait(false);
                    return CreateHttpResponseInfo(ProcessImage(imageByteArray, type));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fail to read image from cache. {Uri} {File}", $"{nameof(uriString)}={uriString}", $"{nameof(cacheFilePath)}={cacheFilePath}");
            }

            using var client = _clientFactory.CreateClient();
            var rawResponse = await client.GetAsync(uriString, cancellationToken).ConfigureAwait(false);
            if (!rawResponse.IsSuccessStatusCode)
            {
                return rawResponse;
            }

            try
            {
                imageByteArray = await rawResponse.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Save image cache uriString={Uri} cacheFilePath={File}", uriString, cacheFilePath);
                await File.WriteAllBytesAsync(cacheFilePath, imageByteArray, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save image cache error. uriString={Uri} cacheFilePath={File}", uriString, cacheFilePath);
                return rawResponse;
            }

            return CreateHttpResponseInfo(ProcessImage(imageByteArray, type));
        }

        /// <summary>
        /// 剪裁图片
        /// </summary>
        /// <param name="input">图片内容</param>
        private byte[] ProcessImage(byte[] input, ImageType imageType)
        {
            _logger.LogInformation($"{nameof(ProcessImage)}: staring...");
            using var memoryStream = new MemoryStream(input);
            memoryStream.Position = 0;
            using var inputStream = new SKManagedStream(memoryStream);
            using var bitmap = SKBitmap.Decode(inputStream);
            var image = SKImage.FromBitmap(bitmap);

            var coverHeight = bitmap.Height;
            var coverWidth = coverHeight * 2 / 3; // 封面宽度
            if (imageType == ImageType.Primary && bitmap.Width > coverWidth) // 需要剪裁
            {
                var face = RecognizeFace(input);
                var x = bitmap.Width - coverWidth; // 默认右边

                if (!face.IsEmpty)
                {
                    if (face.Right >= bitmap.Width / 2) // 右边
                    {
                        x = bitmap.Width - coverWidth;
                    }
                    else if (face.Left <= bitmap.Width / 2) // 左边
                    {
                        x = 0;
                    }
                    else // 居中
                    {
                        x = ((face.Right + face.Left) / 2) - (coverWidth / 2);
                    }
                }

                _logger.LogInformation("{Method}: cut {Width}*{Height} --> x: {Start}", nameof(ProcessImage), bitmap.Width, bitmap.Height, x);
                image = image.Subset(SKRectI.Create(x, 0, coverWidth, coverHeight));
            }

            _logger.LogInformation("{Method}: not need to cut {Width}*{Height}", nameof(ProcessImage), bitmap.Width, coverHeight);
            using var encodedData = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return encodedData.ToArray();
        }

        /// <summary>
        /// 获取人脸的位置，
        /// </summary>
        /// <param name="bytes">图片数据</param>
        /// <returns></returns>
        private Rectangle RecognizeFace(byte[] bytes)
        {
            try
            {
                using var image = new Mat();
                CvInvoke.Imdecode(bytes, ImreadModes.AnyDepth | ImreadModes.AnyColor, image);
                var rectangles = _cascadeClassifier.DetectMultiScale(image);

                return rectangles.Length == 0 ? Rectangle.Empty : rectangles.MaxBy(rectangle => rectangle.Height * rectangle.Width);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fail to recognize face");
                return Rectangle.Empty;
            }
        }

        public static HttpResponseMessage CreateHttpResponseInfo(byte[] bytes, string contentType = "image/jpeg")
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);

            return response;
        }

        public void Dispose()
        {
            _cascadeClassifier.Dispose();
        }
    }
}
