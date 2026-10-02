using FluentValidation;
using RhinoSurfaceMapper.Application.Interfaces;
using RhinoSurfaceMapper.Application.Mediator;
using RhinoSurfaceMapper.Domain.Exceptions;
using RhinoSurfaceMapper.Domain.Interfaces;

namespace RhinoSurfaceMapper.Application.Features.MapSession;

/// <summary>
/// Opens a saved map file, replacing the live session's state, ported from
/// <c>MapperWindow.load_map</c>/<c>MapperState.load</c>. Resolving unsaved changes to the map
/// currently open is the caller's responsibility (<see cref="ResolveUnsavedChanges"/>) — this
/// command always installs <see cref="Command.Path"/> unconditionally once it loads and
/// validates, matching Python's own "picker already closed the previous map's disposition
/// question before calling <c>state.load</c>" sequencing.
/// </summary>
public static class LoadMap
{
    /// <summary>Loads the map document at <see cref="Path"/> and installs it as the live session.</summary>
    public sealed record Command : ICommand<Response>
    {
        /// <summary>Absolute path to the map JSON file to open.</summary>
        public required string Path { get; init; }
    }

    /// <summary>Requires a non-empty <see cref="Command.Path"/>.</summary>
    public sealed class Validator : AbstractValidator<Command>
    {
        /// <summary>Creates the validator.</summary>
        public Validator() => RuleFor(command => command.Path).NotEmpty();
    }

    /// <summary>Loads and installs the requested map file.</summary>
    public sealed class Handler(
        IMapRepository mapRepository,
        IMapSessionStore store,
        IMapSessionNotifier notifier,
        IMapTransitionCoordinator coordinator,
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

            Domain.Entities.MapSession loaded;
            try
            {
                loaded = await mapRepository.LoadAsync(command.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (FileNotFoundException)
            {
                return new Response { Path = null, Result = Result.NotFound };
            }
            catch (MapValidationException)
            {
                return new Response { Path = null, Result = Result.InvalidMap };
            }
            catch (IOException)
            {
                return new Response { Path = null, Result = Result.NotFound };
            }

            await store.MutateAsync(session =>
            {
                session.InstallFrom(loaded);
                session.CurrentFilePath = command.Path;
            }, cancellationToken).ConfigureAwait(false);

            // Ported from Python's install_loaded_map: a manual open supersedes any
            // background-telemetry transition that was still pending against the map the user
            // just replaced — clear_transition_state() runs unconditionally on a successful
            // load, not only when the freshly opened map happens to correspond to live telemetry,
            // so a stale pending transition can never later activate against a map the user no
            // longer has open.
            coordinator.Reset();

            notifier.NotifySessionChanged();
            return new Response { Path = command.Path, Result = Result.Success };
        }
    }

    /// <summary>Outcome of opening a map.</summary>
    public sealed record Response
    {
        /// <summary>The path installed, or <see langword="null"/> on failure.</summary>
        public required string? Path { get; init; }

        /// <summary>The outcome.</summary>
        public required Result Result { get; init; }
    }

    /// <summary>Possible <see cref="LoadMap"/> outcomes.</summary>
    public enum Result
    {
        /// <summary>The map was loaded and installed.</summary>
        Success,

        /// <summary>The file did not exist or could not be read.</summary>
        NotFound,

        /// <summary>The file's contents failed <c>MapValidator</c>'s structural/range rules.</summary>
        InvalidMap,
    }
}
