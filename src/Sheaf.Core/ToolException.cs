namespace Sheaf.Core;

/// <summary>An error whose message is written for the person using the app and can be shown as is.</summary>
public class ToolException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The file needs a password, or the one given was wrong.</summary>
public sealed class PasswordException(string message, Exception? inner = null) : ToolException(message, inner);
