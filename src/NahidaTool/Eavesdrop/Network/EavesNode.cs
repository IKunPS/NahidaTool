using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Text;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Globalization;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using NahidaTool.Models.Service;

//using BrotliSharpLib;

namespace NahidaTool.Eavesdrop.Network
{
    public partial class EavesNode : IDisposable
    {
        private SslStream? _secureStream;
        private readonly TcpClient _client;
        private readonly Certifier _certifier;
        private static readonly Regex _responseCookieSplitter;

        public bool IsSecure => (_secureStream != null);

        static EavesNode()
        {
            _responseCookieSplitter = new Regex(",(?! )");
        }
        public EavesNode(Certifier certifier, TcpClient client)
        {
            _client = client;
            _certifier = certifier;

            _client.NoDelay = true;
        }

        public Task<HttpWebRequest> ReadRequestAsync()
        {
            return ReadRequestAsync(null);
        }
        private async Task<HttpWebRequest?> ReadRequestAsync(Uri? baseUri)
        {
            string? method = null;
            var headers = new List<string>();
            string? requestUrl = baseUri?.OriginalString;

            string? command = ReadNonBufferedLine();
            if (string.IsNullOrWhiteSpace(command)) return null;

            string[] values = command.Split(' ');
            if (values.Length < 2) return null;

            method = values[0];
            requestUrl = baseUri == null ? values[1] : new Uri(baseUri, values[1]).AbsoluteUri;
            while (_client.Connected)
            {
                string? header = ReadNonBufferedLine();
                if (string.IsNullOrWhiteSpace(header)) break;

                headers.Add(header);
            }

            if (method == "CONNECT")
            {
                baseUri = new Uri("https://" + requestUrl);

                await SendResponseAsync(HttpStatusCode.OK).ConfigureAwait(false);
                Uri? gameOrigin = await OpenTunnelAsync(baseUri).ConfigureAwait(false);
                return gameOrigin == null ? null : await ReadRequestAsync(gameOrigin).ConfigureAwait(false);
            }
            else return CreateRequest(method, headers, new Uri(requestUrl));
        }

        public async Task<ByteArrayContent> ReadRequestContentAsync(WebRequest request)
        {
            byte[]? payload = await GetPayload(GetStream(), request.ContentLength).ConfigureAwait(false);
            if (payload == null) return null;

            //if (request.Headers[HttpRequestHeader.ContentEncoding] == "br")
            //{
            //    request.Headers[HttpRequestHeader.ContentEncoding] = ""; // No longer encoded.
            //    payload = Brotli.DecompressBuffer(payload, 0, payload.Length);
            //}
            return new ByteArrayContent(payload);
        }
        public async Task WriteRequestContentAsync(WebRequest request, HttpContent content)
        {
            byte[]? payload = null;
            if (content is StreamContent streamContent)
            {
                // TODO:
                throw new NotSupportedException();
            }
            else payload = await content.ReadAsByteArrayAsync().ConfigureAwait(false);

            //if (request.Headers[HttpRequestHeader.ContentEncoding] == "br")
            //{
            //    payload = Brotli.CompressBuffer(payload, 0, payload.Length);
            //}

            request.ContentLength = payload.Length;
            using (Stream output = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await output.WriteAsync(payload, 0, payload.Length).ConfigureAwait(false);
            }
        }

        public Task SendResponseAsync(WebResponse response, HttpContent content)
        {
            string description = "OK";
            var status = HttpStatusCode.OK;
            if (response is HttpWebResponse httpResponse)
            {
                status = httpResponse.StatusCode;
                description = httpResponse.StatusDescription;
            }
            return SendResponseAsync(status, description, response.Headers, content);
        }
        public Task SendResponseAsync(HttpStatusCode status, string description = null)
        {
            return SendResponseAsync(status, (description ?? status.ToString()), null, null);
        }
        private const int RelayBufferSize = 65536; // 64KB

        public async Task SendResponseAsync(HttpStatusCode status, string description, WebHeaderCollection headers, HttpContent content)
        {
            var headerBuilder = new StringBuilder();
            headerBuilder.AppendLine($"HTTP/{HttpVersion.Version10} {(int)status} {description}");
            if (headers != null)
            {
                foreach (string header in headers.AllKeys)
                {
                    if (header == "Transfer-Encoding") continue;

                    string value = headers[header];
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    if (header.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (string setCookie in _responseCookieSplitter.Split(value))
                        {
                            headerBuilder.AppendLine($"{header}: {setCookie}");
                        }
                    }
                    else headerBuilder.AppendLine($"{header}: {value}");
                }
            }
            headerBuilder.AppendLine();

            // 优化: 用 ArrayPool 替代直接分配 byte[]
            string headerString = headerBuilder.ToString();
            int headerByteCount = Encoding.UTF8.GetByteCount(headerString);
            byte[] rentedHeader = ArrayPool<byte>.Shared.Rent(headerByteCount);
            try
            {
                Encoding.UTF8.GetBytes(headerString, rentedHeader);
                await GetStream().WriteAsync(rentedHeader.AsMemory(0, headerByteCount)).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedHeader);
            }

            if (content != null)
            {
                Stream input = await content.ReadAsStreamAsync().ConfigureAwait(false);

                // 优化: 64KB ArrayPool 缓冲区替代每次 new byte[8192]
                byte[] buffer = ArrayPool<byte>.Shared.Rent(RelayBufferSize);
                try
                {
                    int bytesRead;
                    while ((bytesRead = await input.ReadAsync(
                        buffer.AsMemory(0, RelayBufferSize)).ConfigureAwait(false)) > 0)
                    {
                        if (!_client.Connected) return;
                        await GetStream().WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        public Stream GetStream()
        {
            return (Stream?)_secureStream ?? _tunnelTransport ?? _client.GetStream();
        }
        private StreamWriter WrapStreamWriter()
        {
            return new StreamWriter(GetStream(), Encoding.UTF8, 1024, true);
        }
        private StreamReader WrapStreamReader(int bufferSize = 1024)
        {
            return new StreamReader(GetStream(), Encoding.UTF8, true, bufferSize, true);
        }

        private string? ReadNonBufferedLine()
        {
            // 优化: 使用栈上 span 缓冲一行，避免逐字符字符串拼接和 BinaryReader 分配
            Span<byte> lineBuffer = stackalloc byte[8192];
            int pos = 0;
            Stream stream = GetStream();

            try
            {
                while (pos < lineBuffer.Length)
                {
                    int b = stream.ReadByte();
                    if (b == -1) break; // EOF
                    if (b == '\n')
                    {
                        int len = pos > 0 && lineBuffer[pos - 1] == '\r' ? pos - 1 : pos;
                        return len > 0 ? Encoding.UTF8.GetString(lineBuffer[..len]) : string.Empty;
                    }
                    lineBuffer[pos++] = (byte)b;
                }
            }
            catch (EndOfStreamException) { }
            catch (IOException) { }

            return pos > 0 ? Encoding.UTF8.GetString(lineBuffer[..pos]) : null;
        }
        private IEnumerable<Cookie> GetCookies(string cookieHeader, string host)
        {
            foreach (string cookie in cookieHeader.Split(';'))
            {
                int nameEndIndex = cookie.IndexOf('=');
                if (nameEndIndex == -1) continue;

                string name = cookie.Substring(0, nameEndIndex).Trim();
                string value = cookie.Substring(nameEndIndex + 1).Trim();

                yield return new Cookie(name, value, "/", host);
            }
        }
        private HttpWebRequest CreateRequest(string method, List<string> headers, Uri requestUri)
        {
            HttpWebRequest request = WebRequest.CreateHttp(requestUri);
            request.ProtocolVersion = HttpVersion.Version10;
            request.CookieContainer = new CookieContainer();
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.Method = method;
            request.Proxy = null;

            foreach (string header in headers)
            {
                int delimiterIndex = header.IndexOf(':');
                if (delimiterIndex == -1) continue;

                string name = header.Substring(0, delimiterIndex);
                string value = header.Substring(delimiterIndex + 2);
                switch (name.ToLower())
                {
                    case "range":
                    case "expect":
                    case "keep-alive":
                    case "connection":
                    case "proxy-connection": break;

                    case "host": request.Host = value; break;
                    case "accept": request.Accept = value; break;
                    case "referer": request.Referer = value; break;
                    case "user-agent": request.UserAgent = value; break;
                    case "content-type": request.ContentType = value; break;

                    case "content-length":
                    {
                        request.ContentLength =
                            long.Parse(value, CultureInfo.InvariantCulture);

                        break;
                    }
                    case "cookie":
                    {
                        foreach (Cookie cookie in GetCookies(value, request.Host))
                        {
                            try
                            {
                                request.CookieContainer.Add(cookie);
                            }
                            catch (CookieException) { }
                        }
                        request.Headers[name] = value;
                        break;
                    }
                    case "if-modified-since":
                    {
                        request.IfModifiedSince = DateTime.Parse(
                            value.Split(';')[0], CultureInfo.InvariantCulture);

                        break;
                    }

                    case "date":
                        if (long.TryParse(value, out var timestamp))
                        {
                            request.Date = timestamp > 10_000_000_000L
                                ? DateTimeOffset.FromUnixTimeMilliseconds(timestamp).DateTime
                                : DateTimeOffset.FromUnixTimeSeconds(timestamp).DateTime;
                        }
                        else
                        {
                            request.Date = DateTime.Parse(value);
                        }
                        break;

                    default:
                    request.Headers[name] = value; break;
                }
            }
            return request;
        }
        
        public void Dispose()
        {
            Dispose(true);
        }
        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                GetStream().Dispose();
                _client.Dispose();
            }
        }

        public static StreamContent? ReadResponseContent(WebResponse response)
        {
            if (response.ContentLength == 0)
            {
                response.GetResponseStream().Dispose();
                return null;
            }

            Stream input = response.GetResponseStream();
            //if (response is HttpWebResponse httpResponse && !string.IsNullOrWhiteSpace(httpResponse.ContentEncoding))
            //{
            //    switch (httpResponse.ContentEncoding)
            //    {
            //        //case "br": input = new BrotliStream(input, CompressionMode.Decompress); break;
            //        case "gzip": input = new GZipStream(input, CompressionMode.Decompress); break;
            //        case "deflate": input = new DeflateStream(input, CompressionMode.Decompress); break;
            //    }
            //    response.Headers.Remove(HttpResponseHeader.ContentLength);
            //    response.Headers.Remove(HttpResponseHeader.ContentEncoding);
            //    response.Headers.Add(HttpResponseHeader.TransferEncoding, "chunked");
            //}
            return new StreamContent(input, response.ContentLength > 0 ? (int)response.ContentLength : 4096);
        }
        public static async Task<byte[]?> GetPayload(Stream input, long length)
        {
            if (length < 1) return null;

            // 优化: 使用 ArrayPool 避免大对象直接分配 (>85KB 进 LOH)
            byte[] payload = ArrayPool<byte>.Shared.Rent((int)length);
            int totalBytesRead = 0;
            int nullBytesReadCount = 0;

            try
            {
                do
                {
                    int bytesLeft = (int)length - totalBytesRead;
                    int bytesRead = await input.ReadAsync(
                        payload.AsMemory(totalBytesRead, bytesLeft)).ConfigureAwait(false);

                    if (bytesRead > 0)
                    {
                        nullBytesReadCount = 0;
                        totalBytesRead += bytesRead;
                    }
                    else if (++nullBytesReadCount >= 2)
                    {
                        return null;
                    }
                }
                while (totalBytesRead != length);

                // 复制到精确大小数组后归还池
                byte[] result = new byte[length];
                Array.Copy(payload, result, length);
                return result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }
}
