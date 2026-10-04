namespace Canary.Runner.State;

/// <summary>
/// 実サービスの同じ応答を新規DBと前回DBに通し、比較中の外部データ変更を避ける。
/// </summary>
internal sealed class SyncHttpResponses
{
    private readonly Dictionary<string, CapturedResponse> responses = new(StringComparer.Ordinal);

    internal DelegatingHandler CreateHandler(bool capture) => new ResponseHandler(this, capture);

    private sealed record CapturedResponse(System.Net.HttpStatusCode Status,
        byte[] Body, Dictionary<string, string[]> Headers, Dictionary<string, string[]> ContentHeaders)
    {
        internal HttpResponseMessage Create()
        {
            var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Body) };
            foreach (var header in Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var header in ContentHeaders) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return response;
        }
    }

    private sealed class ResponseHandler(SyncHttpResponses owner, bool capture) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get) throw new InvalidOperationException("同期チェックではGETのみ許可します。");
            var key = request.RequestUri!.AbsoluteUri;
            if (owner.responses.TryGetValue(key, out var captured)) return captured.Create();
            if (!capture) throw new InvalidOperationException("新規取得に含まれない要求が増分取得で発生しました。");
            var response = await base.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                    owner.responses.Add(key, new CapturedResponse(response.StatusCode, body,
                        response.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray()),
                        response.Content.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray())));
                }
                catch { response.Dispose(); throw; }
            }
            return response;
        }
    }
}
