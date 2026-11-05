using System.Text.Json.Serialization;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.S3Events;
using Amazon.Lambda.Serialization.SystemTextJson;
using PdfLambda;

// One executable, two functions: template.yaml sets PDF_FUNCTION on each.
var serializer = new SourceGeneratorLambdaJsonSerializer<LambdaJson>();
string function = Environment.GetEnvironmentVariable("PDF_FUNCTION") ?? "";
using LambdaBootstrap bootstrap = function switch
{
    "optimize" => LambdaBootstrapBuilder.Create<S3Event>(OptimizeHandler.HandleAsync, serializer).Build(),
    "html" => LambdaBootstrapBuilder.Create<APIGatewayHttpApiV2ProxyRequest, APIGatewayHttpApiV2ProxyResponse>(HtmlHandler.HandleAsync, serializer).Build(),
    _ => throw new InvalidOperationException("Set PDF_FUNCTION to optimize or html."),
};
await bootstrap.RunAsync();

namespace PdfLambda
{
    /// <summary>The event types, serialized by source generated code: no reflection under Native AOT.</summary>
    [JsonSerializable(typeof(S3Event))]
    [JsonSerializable(typeof(APIGatewayHttpApiV2ProxyRequest))]
    [JsonSerializable(typeof(APIGatewayHttpApiV2ProxyResponse))]
    internal sealed partial class LambdaJson : JsonSerializerContext;
}
