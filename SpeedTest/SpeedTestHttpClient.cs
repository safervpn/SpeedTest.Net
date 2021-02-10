using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Serialization;

namespace SpeedTest
{
    internal class SpeedTestHttpClient : HttpClient
    {
        private const int _maxResponseContentBufferSize = 100 * 1024 * 1024;

        private const int UTF8CodePage = 65001;
        private const int UTF8PreambleLength = 3;
        private const byte UTF8PreambleByte0 = 0xEF;
        private const byte UTF8PreambleByte1 = 0xBB;
        private const byte UTF8PreambleByte2 = 0xBF;
        private const int UTF8PreambleFirst2Bytes = 0xEFBB;

        private const int UTF32CodePage = 12000;
        private const int UTF32PreambleLength = 4;
        private const byte UTF32PreambleByte0 = 0xFF;
        private const byte UTF32PreambleByte1 = 0xFE;
        private const byte UTF32PreambleByte2 = 0x00;
        private const byte UTF32PreambleByte3 = 0x00;
        private const int UTF32OrUnicodePreambleFirst2Bytes = 0xFFFE;

        private const int UnicodeCodePage = 1200;
        private const int UnicodePreambleLength = 2;
        private const byte UnicodePreambleByte0 = 0xFF;
        private const byte UnicodePreambleByte1 = 0xFE;

        private const int BigEndianUnicodeCodePage = 1201;
        private const int BigEndianUnicodePreambleLength = 2;
        private const byte BigEndianUnicodePreambleByte0 = 0xFE;
        private const byte BigEndianUnicodePreambleByte1 = 0xFF;
        private const int BigEndianUnicodePreambleFirst2Bytes = 0xFEFF;

        public int ConnectionLimit { get; set; }

        public SpeedTestHttpClient()
        {
            var osInfo = Environment.OSVersion.VersionString;

            var arch = System.Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");

            DefaultRequestHeaders.Add("Accept", "text/html, application/xhtml+xml, */*");
            DefaultRequestHeaders.Add("User-Agent", string.Join(" ", new string[]
            {
                "Mozilla/5.0",
                $"({osInfo}; U; {arch}; en-us)",
                ".Net Framework 4.5",
                "(KHTML, like Gecko)",
                $"SpeedTest.Net/{typeof(ISpeedTestClient).Assembly.GetName().Version}"
            }));
        }

        public async Task<T> GetConfig<T>(string url)
        {
            var data = await GetStringAsync(AddTimeStamp(new Uri(url)));
            var xmlSerializer = new XmlSerializer(typeof(T));
            using (var reader = new StringReader(data))
            {
                return (T) xmlSerializer.Deserialize(reader);
            }
        }

        private static Uri AddTimeStamp(Uri address)
        {
            var uriBuilder = new UriBuilder(address);
            var query = HttpUtility.ParseQueryString(uriBuilder.Query);
            query["x"] = DateTime.Now.ToFileTime().ToString(CultureInfo.InvariantCulture);
            uriBuilder.Query = query.ToString();
            return uriBuilder.Uri;
        }

        public async Task<byte[]> GetByteArrayAsync(string requestUri, CancellationToken cancellationToken = default)
        {
            // Wait for the response message.
            using (HttpResponseMessage responseMessage = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false))
            {
                // Make sure it completed successfully.
                responseMessage.EnsureSuccessStatusCode();

                // Get the response content.
                HttpContent c = responseMessage.Content;
                if (c != null)
                {
                    HttpContentHeaders headers = c.Headers;
                    using (Stream responseStream = await c.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        long? contentLength = headers.ContentLength;
                        MemoryStream buffer; // declared here to share the state machine field across both if/else branches

                        if (contentLength.HasValue)
                        {
                            // If we got a content length, then we assume that it's correct and create a MemoryStream
                            // to which the content will be transferred.  That way, assuming we actually get the exact
                            // amount we were expecting, we can simply return the MemoryStream's underlying buffer.
                            buffer = new MemoryStream(Math.Max(_maxResponseContentBufferSize,
                                (int) contentLength.GetValueOrDefault()));
                            await responseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                            if (buffer.Length > 0)
                            {
                                return buffer.ToArray();
                            }
                        }
                        else
                        {
                            // If we didn't get a content length, then we assume we're going to have to grow
                            // the buffer potentially several times and that it's unlikely the underlying buffer
                            // at the end will be the exact size needed, in which case it's more beneficial to use
                            // ArrayPool buffers and copy out to a new array at the end.
                            buffer = new MemoryStream(_maxResponseContentBufferSize);
                            try
                            {
                                await responseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                                if (buffer.Length > 0)
                                {
                                    return buffer.ToArray();
                                }
                            }
                            finally { buffer.Dispose(); }
                        }
                    }
                }

                // No content to return.
                return Array.Empty<byte>();
            }
        }


        public async Task<string> GetStringAsync(string requestUri, CancellationToken cancellationToken)
        {
            // Wait for the response message.
            using (HttpResponseMessage responseMessage = await GetAsync(requestUri, cancellationToken).ConfigureAwait(false))
            {
                // Make sure it completed successfully.
                responseMessage.EnsureSuccessStatusCode();

                // Get the response content.
                HttpContent c = responseMessage.Content;
                if (c != null)
                {
                    HttpContentHeaders headers = c.Headers;

                    // Since the underlying byte[] will never be exposed, we use an ArrayPool-backed
                    // stream to which we copy all of the data from the response.
                    using (Stream responseStream = await c.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var buffer = new MemoryStream(Math.Max(_maxResponseContentBufferSize, (int)headers.ContentLength.GetValueOrDefault())))
                    {
                        await responseStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

                        if (buffer.Length > 0)
                        {
                            // Decode and return the data from the buffer.
                            return ReadBufferAsString(buffer.GetBuffer(), headers);
                        }
                    }
                }

                // No content to return.
                return string.Empty;
            }
        }

        private static bool BufferHasPrefix(ArraySegment<byte> buffer, byte[] prefix)
        {
            byte[]? byteArray = buffer.Array;
            if (prefix == null || byteArray == null || prefix.Length > buffer.Count || prefix.Length == 0)
                return false;

            for (int i = 0, j = buffer.Offset; i < prefix.Length; i++, j++)
            {
                if (prefix[i] != byteArray[j])
                    return false;
            }

            return true;
        }

        private static int GetPreambleLength(ArraySegment<byte> buffer, Encoding encoding)
        {
            byte[]? data = buffer.Array;
            int offset = buffer.Offset;
            int dataLength = buffer.Count;

            Debug.Assert(data != null);
            Debug.Assert(encoding != null);

            switch (encoding.CodePage)
            {
                case UTF8CodePage:
                    return (dataLength >= UTF8PreambleLength
                            && data[offset + 0] == UTF8PreambleByte0
                            && data[offset + 1] == UTF8PreambleByte1
                            && data[offset + 2] == UTF8PreambleByte2) ? UTF8PreambleLength : 0;
                case UTF32CodePage:
                    return (dataLength >= UTF32PreambleLength
                            && data[offset + 0] == UTF32PreambleByte0
                            && data[offset + 1] == UTF32PreambleByte1
                            && data[offset + 2] == UTF32PreambleByte2
                            && data[offset + 3] == UTF32PreambleByte3) ? UTF32PreambleLength : 0;
                case UnicodeCodePage:
                    return (dataLength >= UnicodePreambleLength
                            && data[offset + 0] == UnicodePreambleByte0
                            && data[offset + 1] == UnicodePreambleByte1) ? UnicodePreambleLength : 0;

                case BigEndianUnicodeCodePage:
                    return (dataLength >= BigEndianUnicodePreambleLength
                            && data[offset + 0] == BigEndianUnicodePreambleByte0
                            && data[offset + 1] == BigEndianUnicodePreambleByte1) ? BigEndianUnicodePreambleLength : 0;

                default:
                    byte[] preamble = encoding.GetPreamble();
                    return BufferHasPrefix(buffer, preamble) ? preamble.Length : 0;
            }
        }

        private static bool TryDetectEncoding(ArraySegment<byte> buffer, [NotNullWhen(true)] out Encoding? encoding, out int preambleLength)
        {
            byte[]? data = buffer.Array;
            int offset = buffer.Offset;
            int dataLength = buffer.Count;

            Debug.Assert(data != null);

            if (dataLength >= 2)
            {
                int first2Bytes = data[offset + 0] << 8 | data[offset + 1];

                switch (first2Bytes)
                {
                    case UTF8PreambleFirst2Bytes:
                        if (dataLength >= UTF8PreambleLength && data[offset + 2] == UTF8PreambleByte2)
                        {
                            encoding = Encoding.UTF8;
                            preambleLength = UTF8PreambleLength;
                            return true;
                        }
                        break;

                    case UTF32OrUnicodePreambleFirst2Bytes:
                        // UTF32 not supported on Phone
                        if (dataLength >= UTF32PreambleLength && data[offset + 2] == UTF32PreambleByte2 && data[offset + 3] == UTF32PreambleByte3)
                        {
                            encoding = Encoding.UTF32;
                            preambleLength = UTF32PreambleLength;
                        }
                        else
                        {
                            encoding = Encoding.Unicode;
                            preambleLength = UnicodePreambleLength;
                        }
                        return true;

                    case BigEndianUnicodePreambleFirst2Bytes:
                        encoding = Encoding.BigEndianUnicode;
                        preambleLength = BigEndianUnicodePreambleLength;
                        return true;
                }
            }

            encoding = null;
            preambleLength = 0;
            return false;
        }

        internal static string ReadBufferAsString(ArraySegment<byte> buffer, HttpContentHeaders headers)
        {
            // We don't validate the Content-Encoding header: If the content was encoded, it's the caller's
            // responsibility to make sure to only call ReadAsString() on already decoded content. E.g. if the
            // Content-Encoding is 'gzip' the user should set HttpClientHandler.AutomaticDecompression to get a
            // decoded response stream.

            Encoding? encoding = null;
            int bomLength = -1;

            string? charset = headers.ContentType?.CharSet;

            // If we do have encoding information in the 'Content-Type' header, use that information to convert
            // the content to a string.
            if (charset != null)
            {
                try
                {
                    // Remove at most a single set of quotes.
                    if (charset.Length > 2 &&
                        charset[0] == '\"' &&
                        charset[charset.Length - 1] == '\"')
                    {
                        encoding = Encoding.GetEncoding(charset.Substring(1, charset.Length - 2));
                    }
                    else
                    {
                        encoding = Encoding.GetEncoding(charset);
                    }

                    // Byte-order-mark (BOM) characters may be present even if a charset was specified.
                    bomLength = GetPreambleLength(buffer, encoding);
                }
                catch (ArgumentException e)
                {
                    throw new InvalidOperationException(e.Message, e);
                }
            }

            // If no content encoding is listed in the ContentType HTTP header, or no Content-Type header present,
            // then check for a BOM in the data to figure out the encoding.
            if (encoding == null)
            {
                if (!TryDetectEncoding(buffer, out encoding, out bomLength))
                {
                    // Use the default encoding (UTF8) if we couldn't detect one.
                    encoding = Encoding.UTF8;

                    // We already checked to see if the data had a UTF8 BOM in TryDetectEncoding
                    // and DefaultStringEncoding is UTF8, so the bomLength is 0.
                    bomLength = 0;
                }
            }

            // Drop the BOM when decoding the data.
            return encoding.GetString(buffer.Array!, buffer.Offset + bomLength, buffer.Count - bomLength);
        }
    }
}
