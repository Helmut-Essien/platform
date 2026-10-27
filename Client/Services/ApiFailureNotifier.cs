namespace Platform.Client.Services;

/// <summary>
/// Lets the API client report a failed background load (network error, server error, bad JSON) to the UI shell
/// so it can show one snackbar instead of every page crashing with an unhandled exception.
/// </summary>
public class ApiFailureNotifier
{
    public event Action<string>? Failed;

    public void Report(string message) => Failed?.Invoke(message);
}
