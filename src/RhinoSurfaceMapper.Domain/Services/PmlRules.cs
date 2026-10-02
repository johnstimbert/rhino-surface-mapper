using System.Text.RegularExpressions;
using RhinoSurfaceMapper.Domain.Constants;
using RhinoSurfaceMapper.Domain.ValueObjects;

namespace RhinoSurfaceMapper.Domain.Services;

/// <summary>
/// PML discovery, matching, and filename rules, ported from <c>map_pml.py</c>. Depends only on
/// plain values, regexes and <see cref="PlanetGeometry.SurfaceDistance"/> — no file I/O, matching
/// the Python module's own "must not import PySide6" / persistence-boundary discipline.
/// </summary>
/// <remarks>
/// File scanning (<c>MatchingCandidates</c>, <c>NextJohnDoeId</c>) is written against an injected
/// <c>loader</c>-style delegate exactly as the Python functions are, so this type
/// still has no dependency on a filesystem abstraction; the Phase 2 <c>IMapRepository</c>
/// implementation supplies the loader and the actual <see cref="System.IO.Directory"/> scan.
/// </remarks>
public static class PmlRules
{
    private static readonly Regex InvalidFilenameCharacters = new(@"[<>:""/\\|?*\x00-\x1f]", RegexOptions.Compiled);
    private static readonly Regex LegacyNamePattern = new(@"\A(.+?)\s+\[[^\]]+\](?:\s+v\d+)?\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LegacyCentreMarkerPattern = new(@"\ACentro\s*\[([^\]]+)\]\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex JohnDoePattern = new(@"\AJD(\d+)\z", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Returns whether a position belongs to the supplied map/PML location, ported from
    /// <c>map_pml.corresponds_to_map</c>.
    /// </summary>
    /// <remarks>
    /// System and body identity must match case-insensitively (<see cref="StringComparison.OrdinalIgnoreCase"/>,
    /// matching Python's <c>str.casefold()</c> comparison for this ASCII/Latin identifier domain)
    /// and the stored PML centre must be available. The fixed
    /// <see cref="MapperConstants.PmlMatchDistanceMetres"/> correspondence radius is inclusive: a
    /// position exactly on the boundary still belongs to the map.
    /// </remarks>
    public static bool CorrespondsToMap(PmlCandidate candidate, string system, string body, double lat, double lon)
    {
        if (!string.Equals(candidate.System, system, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(candidate.Body, body, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (candidate.PmlCenterLat is not double centerLat || candidate.PmlCenterLon is not double centerLon)
        {
            return false;
        }

        double distance = PlanetGeometry.SurfaceDistance(candidate.Radius, lat, lon, centerLat, centerLon);
        return distance <= MapperConstants.PmlMatchDistanceMetres;
    }

    /// <summary>
    /// Returns a readable filename component safe for Windows paths, ported from
    /// <c>map_pml.safe_filename_component</c>.
    /// </summary>
    /// <remarks>
    /// Windows-invalid characters (<c>&lt;&gt;:"/\|?*</c> and control codes) are replaced with
    /// underscores, and trailing dots/spaces are removed because Windows normalises them
    /// unpredictably. An empty result falls back to <c>"Sem nome"</c>, matching Python's
    /// <c>cleaned or 'Sem nome'</c>.
    /// </remarks>
    public static string SafeFilenameComponent(object? value)
    {
        string text = value?.ToString() ?? string.Empty;
        string cleaned = InvalidFilenameCharacters.Replace(text, "_").Trim().TrimEnd('.', ' ');
        return cleaned.Length > 0 ? cleaned : "Sem nome";
    }

    /// <summary>Builds the canonical <c>"Body [PML].json"</c> filename, ported from <c>map_pml.pml_filename</c>.</summary>
    public static string PmlFileName(string body, string pmlId) =>
        $"{SafeFilenameComponent(body)} [{SafeFilenameComponent(pmlId)}].json";

    /// <summary>
    /// Returns the canonical PML path, ported from <c>map_pml.pml_path</c>, or
    /// <see langword="null"/> until the map's identity (system, body and PML id) is fully known.
    /// </summary>
    public static string? PmlPath(string baseDirectory, string system, string body, string pmlId)
    {
        if (string.IsNullOrWhiteSpace(system) || string.IsNullOrWhiteSpace(body) || string.IsNullOrWhiteSpace(pmlId))
        {
            return null;
        }

        return Path.Combine(baseDirectory, SafeFilenameComponent(system), PmlFileName(body, pmlId));
    }

    /// <summary>
    /// Recovers PML metadata from legacy paths and <c>Centro [id]</c> markers, ported from
    /// <c>map_pml.infer_legacy_pml</c>. Older maps may encode the system in the parent directory,
    /// the body/PML in the filename, and the PML centre as a marker named <c>Centro [id]</c>;
    /// this method restores those fields in place so old files participate in modern matching.
    /// </summary>
    /// <param name="candidate">The loaded (but not necessarily fully valid) candidate, mutated in place.</param>
    /// <param name="path">The file path the candidate was loaded from.</param>
    /// <param name="system">Current star system, used to confirm the parent directory encodes it.</param>
    /// <param name="body">Current body name, used as a fallback when the filename carries no legacy pattern.</param>
    public static void InferLegacyPml(PmlCandidate candidate, string path, string system, string body)
    {
        string? parentDirectoryName = Path.GetFileName(Path.GetDirectoryName(path));
        if (candidate.System.Length == 0
            && string.Equals(parentDirectoryName, SafeFilenameComponent(system), StringComparison.OrdinalIgnoreCase))
        {
            candidate.System = system;
        }

        string stem = Path.GetFileNameWithoutExtension(path);
        var legacyNameMatch = LegacyNamePattern.Match(stem);

        if (candidate.PmlId.Length == 0 && candidate.PmlCenterLat is null && legacyNameMatch.Success)
        {
            candidate.Body = legacyNameMatch.Groups[1].Value.Trim();
        }
        else if (candidate.Body.Length == 0)
        {
            candidate.Body = body;
        }

        foreach (var mark in candidate.Marks)
        {
            // Legacy centre markers are the only persisted source of the old PML identifier and
            // geographic centre.
            var match = LegacyCentreMarkerPattern.Match((mark.Name ?? string.Empty).Trim());
            if (match.Success && mark.Lat is not null && mark.Lon is not null)
            {
                candidate.PmlId = candidate.PmlId.Length > 0 ? candidate.PmlId : match.Groups[1].Value.Trim();
                candidate.PmlCenterLat ??= mark.Lat;
                candidate.PmlCenterLon ??= mark.Lon;
                break;
            }
        }

        candidate.BodyKey = $"{candidate.System}|{candidate.Body}";
    }

    /// <summary>
    /// Loads and returns nearby compatible candidates, ignoring malformed files, ported from
    /// <c>map_pml.matching_candidates</c>.
    /// </summary>
    /// <param name="paths">Candidate map paths to inspect.</param>
    /// <param name="system">Current star system.</param>
    /// <param name="body">Current body name.</param>
    /// <param name="lat">Current latitude.</param>
    /// <param name="lon">Current longitude.</param>
    /// <param name="loader">Loads a path into a <see cref="PmlCandidate"/>; supplied by the Phase 2 repository.</param>
    /// <returns><c>(distance, path, candidate)</c> tuples sorted from nearest to farthest.</returns>
    /// <remarks>
    /// Python catches <c>(OSError, ValueError, TypeError, KeyError, AttributeError)</c> around
    /// each candidate load — the broad-but-named set of failures an arbitrary malformed external
    /// file can produce. The port catches the equivalent .NET set
    /// (<see cref="IOException"/>, <see cref="Exceptions.MapValidationException"/>,
    /// <see cref="FormatException"/>, <see cref="InvalidOperationException"/>) rather than a bare
    /// <see cref="Exception"/>, per AGENTS.md's "catch specific exceptions" rule.
    /// </remarks>
    public static IReadOnlyList<(double Distance, string Path, PmlCandidate Candidate)> MatchingCandidates(
        IEnumerable<string> paths, string system, string body, double lat, double lon, Func<string, PmlCandidate> loader)
    {
        var matches = new List<(double Distance, string Path, PmlCandidate Candidate)>();
        foreach (string path in paths)
        {
            PmlCandidate candidate;
            try
            {
                candidate = loader(path);
                InferLegacyPml(candidate, path, system, body);
            }
            catch (Exception ex) when (ex is IOException or Exceptions.MapValidationException or FormatException or InvalidOperationException)
            {
                continue;
            }

            if (!CorrespondsToMap(candidate, system, body, lat, lon))
            {
                continue;
            }

            double distance = PlanetGeometry.SurfaceDistance(candidate.Radius, lat, lon, candidate.PmlCenterLat!.Value, candidate.PmlCenterLon!.Value);
            matches.Add((distance, path, candidate));
        }

        return [.. matches.OrderBy(match => match.Distance)];
    }

    /// <summary>Keeps the newest file for each PML identifier, ported from <c>map_pml.newest_by_pml</c>.</summary>
    /// <param name="matches">Candidates to deduplicate, typically the result of <see cref="MatchingCandidates"/>.</param>
    /// <param name="lastWriteTimeUtc">Resolves a candidate path's last-write time; supplied by the Phase 2 repository.</param>
    public static IReadOnlyList<(double Distance, string Path, PmlCandidate Candidate)> NewestByPml(
        IEnumerable<(double Distance, string Path, PmlCandidate Candidate)> matches, Func<string, DateTime> lastWriteTimeUtc)
    {
        var selected = new Dictionary<string, (double Distance, string Path, PmlCandidate Candidate)>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in matches)
        {
            string key = item.Candidate.PmlId;
            if (!selected.TryGetValue(key, out var existing) || lastWriteTimeUtc(item.Path) > lastWriteTimeUtc(existing.Path))
            {
                selected[key] = item;
            }
        }

        return [.. selected.Values];
    }

    /// <summary>Returns the next John Doe identifier for valid maps of the requested body, ported from <c>map_pml.next_john_doe_id</c>.</summary>
    public static string NextJohnDoeId(IEnumerable<string> paths, string system, string body, Func<string, PmlCandidate> loader)
    {
        int highest = 0;
        foreach (string path in paths)
        {
            PmlCandidate candidate;
            try
            {
                candidate = loader(path);
                InferLegacyPml(candidate, path, system, body);
            }
            catch (Exception ex) when (ex is IOException or Exceptions.MapValidationException or FormatException or InvalidOperationException)
            {
                continue;
            }

            if (!string.Equals(candidate.Body, body, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var match = JohnDoePattern.Match(candidate.PmlId.Trim());
            if (match.Success)
            {
                highest = Math.Max(highest, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return $"JD{highest + 1}";
    }

    /// <summary>
    /// Returns the next version path, ported from <c>map_pml.next_version_path</c>.
    /// </summary>
    /// <remarks>
    /// Version detection uses a regex against the supplied file names instead of a glob pattern,
    /// because square brackets are glob character-class syntax while PML filenames deliberately
    /// contain literal <c>[id]</c> components.
    /// </remarks>
    /// <param name="canonical">The canonical (version-less) map path.</param>
    /// <param name="existingFileNames">File names already present in the canonical path's directory.</param>
    public static string NextVersionPath(string canonical, IEnumerable<string> existingFileNames)
    {
        string stem = Path.GetFileNameWithoutExtension(canonical);
        var pattern = new Regex($"^{Regex.Escape(stem)} v(\\d+)\\.json$", RegexOptions.IgnoreCase);

        int highest = 1;
        foreach (string name in existingFileNames)
        {
            var match = pattern.Match(name);
            if (match.Success)
            {
                highest = Math.Max(highest, int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        string? directory = Path.GetDirectoryName(canonical);
        string fileName = $"{stem} v{highest + 1}.json";
        return directory is null ? fileName : Path.Combine(directory, fileName);
    }
}
