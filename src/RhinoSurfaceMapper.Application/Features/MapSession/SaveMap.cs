using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Interfaces;
using RhinoSurfaceMapper.Domain.Services;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Which target <see cref="SaveMap.Command"/> should write to, ported from the three buttons of
/// Python's <c>save_map</c> dialog (<c>Replace</c>/<c>Save new version</c>) plus the file-picker
/// path taken when the map has never been saved (<c>ExplicitPath</c>).
/// </summary>
public enum SaveMode
{
    /// <summary>Overwrite the map's own canonical/current file.</summary>
    Replace,

    /// <summary>Write a new numbered version file alongside the canonical path.</summary>
    NewVersion,

    /// <summary>Write to <see cref="SaveMap.Command.ExplicitPath"/>, used only when the map has no current/canonical path yet.</summary>
    ExplicitPath,
}

/// <summary>
/// Saves the active map, ported from <c>MapperWindow.save_map</c>/<c>MapperState.save</c>.
/// </summary>
public static class SaveMap
{
    /// <summary>Saves the live session according to <see cref="Mode"/>.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>How to choose the destination path.</summary>
        public required SaveMode Mode { get; init; }

        /// <summary>The destination path when <see cref="Mode"/> is <see cref="SaveMode.ExplicitPath"/>; otherwise ignored.</summary>
        public string? ExplicitPath { get; init; }
    }

    /// <summary>Requires <see cref="Command.ExplicitPath"/> whenever <see cref="Command.Mode"/> is <see cref="SaveMode.ExplicitPath"/>.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() =>
            RuleFor(command => command.ExplicitPath)
                .NotEmpty()
                .When(command => command.Mode == SaveMode.ExplicitPath);
    }

    /// <summary>Resolves the destination path and persists the live session to it.</summary>
    public sealed class Handler(
        IMapRepository mapRepository,
        IAppPaths appPaths,
        IMapSessionStore store,
        IMapSessionNotifier notifier,
        IValidator<Command> validator) : ICommandHandler<Command, Response>
    {
        /// <inheritdoc />
        public async Task<Response> HandleAsync(Command command, CancellationToken cancellationToken = default)
        {
            var validationResult = await validator.ValidateAsync(command, cancellationToken).ConfigureAwait(false);
            if (!validationResult.IsValid)
            {
                throw new ValidationException(validationResult.Errors);
            }

            Response response = await store.MutateAsync(
                async (session, ct) =>
                {
                    if (session.ReadOnly)
                    {
                        return new Response { Path = null, Result = Result.ReadOnly };
                    }

                    if (session.CenterLat is null || session.CenterLon is null)
                    {
                        return new Response { Path = null, Result = Result.NoMapCentre };
                    }

                    string? canonical = session.CurrentFilePath
                        ?? PmlRules.PmlPath(appPaths.MapsDirectory, session.System, session.Body, session.PmlId);

                    string path;
                    switch (command.Mode)
                    {
                        case SaveMode.NewVersion:
                            if (canonical is null)
                            {
                                return new Response { Path = null, Result = Result.PathRequired };
                            }

                            string? directory = Path.GetDirectoryName(canonical);
                            var existingFileNames = directory is not null && Directory.Exists(directory)
                                ? Directory.EnumerateFiles(directory).Select(Path.GetFileName)!
                                : Enumerable.Empty<string>();
                            path = PmlRules.NextVersionPath(canonical, existingFileNames!);
                            break;

                        case SaveMode.ExplicitPath:
                            if (string.IsNullOrWhiteSpace(command.ExplicitPath))
                            {
                                return new Response { Path = null, Result = Result.PathRequired };
                            }

                            path = Path.GetExtension(command.ExplicitPath).Length == 0
                                ? command.ExplicitPath + ".json"
                                : command.ExplicitPath;
                            break;

                        default:
                            if (canonical is null)
                            {
                                return new Response { Path = null, Result = Result.PathRequired };
                            }

                            path = canonical;
                            break;
                    }

                    try
                    {
                        string? parent = Path.GetDirectoryName(path);
                        if (!string.IsNullOrEmpty(parent))
                        {
                            Directory.CreateDirectory(parent);
                        }

                        await mapRepository.SaveAsync(session, path, updateSavedAt: true, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        return new Response { Path = null, Result = Result.WriteFailed };
                    }

                    session.CurrentFilePath = path;
                    return new Response { Path = path, Result = Result.Success };
                },
                cancellationToken).ConfigureAwait(false);

            if (response.Result == Result.Success)
            {
                notifier.NotifySessionChanged();
            }

            return response;
        }
    }

    /// <summary>Outcome of saving the active map.</summary>
    public sealed record Response
    {
        /// <summary>The path written to, or <see langword="null"/> on failure.</summary>
        public required string? Path { get; init; }

        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="SaveMap"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The map was written successfully.</summary>
        Success,

        /// <summary>The map is protected or mining-only and cannot be saved.</summary>
        ReadOnly,

        /// <summary>The map has no established centre yet (nothing meaningful to save).</summary>
        NoMapCentre,

        /// <summary>An explicit path (or a canonical path to derive a version from) was required but not available.</summary>
        PathRequired,

        /// <summary>Writing the file failed (disk/permission error).</summary>
        WriteFailed,
    }
}
