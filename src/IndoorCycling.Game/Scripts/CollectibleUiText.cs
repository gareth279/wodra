public static class CollectibleUiText
{
	public const string HelperText = "Collect tokens and earn points while you ride.";

	public static string Score(int score, int collected, int available) => $"Tokens · {score} pts · {collected}/{available}";

	public static string TrailScore(int score, int featureLines, int lap, int lapCollected,
		int available, int cumulativeCollected) =>
		$"Trail {score} pts · {featureLines}/3 jumps\nLap {lap} tokens {lapCollected}/{available} · {cumulativeCollected} total";

	public static string PickupFeedback(string kind, int points, int score) => $"{kind} token +{points} · {score} points";

	public static string ResultSummary(int score, int collected, int available, string result, int featureLines = 0) =>
		$"Game features · {score} points · {collected}/{available} tokens" +
		(featureLines > 0 ? $" · {featureLines} jump lines" : "") + $" · {result}";
}
