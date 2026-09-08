namespace FlashNext.Infrastructure.Windows.Control;

public sealed record ControlApiResult(int StatusCode, string Body, string ContentType = "application/json");
