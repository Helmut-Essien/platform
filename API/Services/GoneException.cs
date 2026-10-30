namespace Platform.Api.Services;

/// <summary>The resource existed but is no longer available (expired pay link). Maps to 410.</summary>
public class GoneException(string message) : Exception(message);
