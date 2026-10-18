public static class CollectibleUiText
{
	public const string HelperText = "Collect tokens and earn points while you ride.";

	public static string Score(int score, int collected, int available) => $"Tokens · {score} pts · {collected}/{available}";

	public static string PickupFeedback(string kind, int points, int score) => $"{kind} token +{points} · {score} points";

	public static string ResultSummary(int score, int collected, int available, string result) =>
		$"Game features · {score} points · {collected}/{available} tokens · {result}";
}
