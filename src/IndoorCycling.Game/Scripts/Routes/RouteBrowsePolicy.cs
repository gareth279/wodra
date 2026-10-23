using System;
using System.Collections.Generic;
using System.Linq;

public enum RouteBrowseSort { Name, Shortest, Longest, LeastAscent, MostAscent }

public sealed record RouteBrowseFilter(string Search = "", string RouteType = "", bool FavoritesOnly = false, RouteBrowseSort Sort = RouteBrowseSort.Name, string Difficulty = "");

public static class RouteBrowsePolicy
{
	public static IReadOnlyList<RouteDefinition> Apply(IEnumerable<RouteDefinition> routes, RouteBrowseFilter filter, ISet<string>? favorites = null)
	{
		IEnumerable<RouteDefinition> result = routes;
		if (!string.IsNullOrWhiteSpace(filter.Search))
		{
			var query = filter.Search.Trim();
			result = result.Where(route => route.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| route.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| route.Id.Contains(query, StringComparison.OrdinalIgnoreCase));
		}
		if (!string.IsNullOrWhiteSpace(filter.RouteType)) result = result.Where(route => route.RouteType.Equals(filter.RouteType, StringComparison.OrdinalIgnoreCase));
		if (!string.IsNullOrWhiteSpace(filter.Difficulty)) result = result.Where(route => route.Difficulty.Equals(filter.Difficulty, StringComparison.OrdinalIgnoreCase));
		if (filter.FavoritesOnly) result = result.Where(route => favorites?.Contains(route.Id) == true);
		return (filter.Sort switch
		{
			RouteBrowseSort.Shortest => result.OrderBy(route => route.DistanceMeters),
			RouteBrowseSort.Longest => result.OrderByDescending(route => route.DistanceMeters),
			RouteBrowseSort.LeastAscent => result.OrderBy(route => route.TotalAscentMeters),
			RouteBrowseSort.MostAscent => result.OrderByDescending(route => route.TotalAscentMeters),
			_ => result.OrderBy(route => route.Name, StringComparer.CurrentCultureIgnoreCase)
		}).ThenBy(route => route.Id, StringComparer.Ordinal).ToArray();
	}
}

public static class RouteFavoritesPolicy
{
	public static HashSet<string> ForProfile(IDictionary<Guid, List<string>> favoritesByProfile, Guid? profileId)
		=> profileId is { } id && favoritesByProfile.TryGetValue(id, out var values)
			? values.Where(value => !string.IsNullOrWhiteSpace(value)).ToHashSet(StringComparer.Ordinal)
			: new HashSet<string>(StringComparer.Ordinal);

	public static bool Toggle(IDictionary<Guid, List<string>> favoritesByProfile, Guid? profileId, string stableRouteId)
	{
		if (profileId is not { } id || string.IsNullOrWhiteSpace(stableRouteId)) return false;
		var favorites = ForProfile(favoritesByProfile, id);
		if (!favorites.Add(stableRouteId)) favorites.Remove(stableRouteId);
		favoritesByProfile[id] = favorites.OrderBy(value => value, StringComparer.Ordinal).ToList();
		return true;
	}
}
