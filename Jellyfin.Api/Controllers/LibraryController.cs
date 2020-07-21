using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.LibraryDtos;
using Jellyfin.Data.Entities;
using MediaBrowser.Common.Progress;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Net;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Book = MediaBrowser.Controller.Entities.Book;
using Movie = Jellyfin.Data.Entities.Movie;
using MusicAlbum = Jellyfin.Data.Entities.MusicAlbum;

namespace Jellyfin.Api.Controllers
{
    /// <summary>
    /// Library Controller.
    /// </summary>
    public class LibraryController : BaseJellyfinApiController
    {
        private readonly IProviderManager _providerManager;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IDtoService _dtoService;
        private readonly IAuthorizationContext _authContext;
        private readonly IActivityManager _activityManager;
        private readonly ILocalizationManager _localization;
        private readonly ILibraryMonitor _libraryMonitor;
        private readonly ILogger<LibraryController> _logger;
        private readonly IServerConfigurationManager _serverConfigurationManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="LibraryController"/> class.
        /// </summary>
        /// <param name="providerManager">Instance of the <see cref="IProviderManager"/> interface.</param>
        /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
        /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
        /// <param name="dtoService">Instance of the <see cref="IDtoService"/> interface.</param>
        /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
        /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
        /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
        /// <param name="libraryMonitor">Instance of the <see cref="ILibraryMonitor"/> interface.</param>
        /// <param name="logger">Instance of the <see cref="ILogger{LibraryController}"/> interface.</param>
        /// <param name="serverConfigurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
        public LibraryController(
            IProviderManager providerManager,
            ILibraryManager libraryManager,
            IUserManager userManager,
            IDtoService dtoService,
            IAuthorizationContext authContext,
            IActivityManager activityManager,
            ILocalizationManager localization,
            ILibraryMonitor libraryMonitor,
            ILogger<LibraryController> logger,
            IServerConfigurationManager serverConfigurationManager)
        {
            _providerManager = providerManager;
            _libraryManager = libraryManager;
            _userManager = userManager;
            _dtoService = dtoService;
            _authContext = authContext;
            _activityManager = activityManager;
            _localization = localization;
            _libraryMonitor = libraryMonitor;
            _logger = logger;
            _serverConfigurationManager = serverConfigurationManager;
        }

        /// <summary>
        /// Get the original file of an item.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <response code="200">File stream returned.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>A <see cref="FileStreamResult"/> with the original file.</returns>
        [HttpGet("/Items/{itemId}/File")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetFile([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return NotFound();
            }

            using var fileStream = new FileStream(item.Path, FileMode.Open, FileAccess.Read);
            return File(fileStream, MimeTypes.GetMimeType(item.Path));
        }

        /// <summary>
        /// Gets critic review for an item.
        /// </summary>
        /// <response code="200">Critic reviews returned.</response>
        /// <returns>The list of critic reviews.</returns>
        [HttpGet("/Items/{itemId}/CriticReviews")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [Obsolete("This endpoint is obsolete.")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<QueryResult<BaseItemDto>> GetCriticReviews()
        {
            return new QueryResult<BaseItemDto>();
        }

        /// <summary>
        /// Get theme songs for an item.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <param name="inheritFromParent">Optional. Determines whether or not parent items should be searched for theme media.</param>
        /// <response code="200">Theme songs returned.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>The item theme songs.</returns>
        [HttpGet("/Items/{itemId}/ThemeSongs")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<ThemeMediaResult> GetThemeSongs(
            [FromRoute] Guid itemId,
            [FromQuery] Guid? userId,
            [FromQuery] bool inheritFromParent = false)
        {
            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;

            var item = itemId.Equals(Guid.Empty)
                ? (!userId.Equals(Guid.Empty)
                    ? _libraryManager.GetUserRootFolder()
                    : _libraryManager.RootFolder)
                : _libraryManager.GetItemById(itemId);

            if (item == null)
            {
                return NotFound("Item not found.");
            }

            IEnumerable<BaseItem> themeItems;

            while (true)
            {
                themeItems = item.GetThemeSongs();

                if (themeItems.Any() || !inheritFromParent)
                {
                    break;
                }

                var parent = item.GetParent();
                if (parent == null)
                {
                    break;
                }

                item = parent;
            }

            var dtoOptions = new DtoOptions().AddClientFields(Request);
            var items = themeItems
                .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user, item))
                .ToArray();

            return new ThemeMediaResult
            {
                Items = items,
                TotalRecordCount = items.Length,
                OwnerId = item.Id
            };
        }

        /// <summary>
        /// Get theme videos for an item.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <param name="inheritFromParent">Optional. Determines whether or not parent items should be searched for theme media.</param>
        /// <response code="200">Theme videos returned.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>The item theme videos.</returns>
        [HttpGet("/Items/{itemId}/ThemeVideos")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<ThemeMediaResult> GetThemeVideos(
            [FromRoute] Guid itemId,
            [FromQuery] Guid? userId,
            [FromQuery] bool inheritFromParent = false)
        {
            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;

            var item = itemId.Equals(Guid.Empty)
                ? (!userId.Equals(Guid.Empty)
                    ? _libraryManager.GetUserRootFolder()
                    : _libraryManager.RootFolder)
                : _libraryManager.GetItemById(itemId);

            if (item == null)
            {
                return NotFound("Item not found.");
            }

            IEnumerable<BaseItem> themeItems;

            while (true)
            {
                themeItems = item.GetThemeVideos();

                if (themeItems.Any() || !inheritFromParent)
                {
                    break;
                }

                var parent = item.GetParent();
                if (parent == null)
                {
                    break;
                }

                item = parent;
            }

            var dtoOptions = new DtoOptions().AddClientFields(Request);
            var items = themeItems
                .Select(i => _dtoService.GetBaseItemDto(i, dtoOptions, user, item))
                .ToArray();

            return new ThemeMediaResult
            {
                Items = items,
                TotalRecordCount = items.Length,
                OwnerId = item.Id
            };
        }

        /// <summary>
        /// Get theme songs and videos for an item.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <param name="inheritFromParent">Optional. Determines whether or not parent items should be searched for theme media.</param>
        /// <response code="200">Theme songs and videos returned.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>The item theme videos.</returns>
        [HttpGet("/Items/{itemId}/ThemeMedia")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<AllThemeMediaResult> GetThemeMedia(
            [FromRoute] Guid itemId,
            [FromQuery] Guid? userId,
            [FromQuery] bool inheritFromParent = false)
        {
            var themeSongs = GetThemeSongs(
                itemId,
                userId,
                inheritFromParent);

            var themeVideos = GetThemeVideos(
                itemId,
                userId,
                inheritFromParent);

            return new AllThemeMediaResult
            {
                ThemeSongsResult = themeSongs?.Value,
                ThemeVideosResult = themeVideos?.Value,
                SoundtrackSongsResult = new ThemeMediaResult()
            };
        }

        /// <summary>
        /// Starts a library scan.
        /// </summary>
        /// <response code="204">Library scan started.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpGet("/Library/Refresh")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public async Task<ActionResult> RefreshLibrary()
        {
            try
            {
                await _libraryManager.ValidateMediaLibrary(new SimpleProgress<double>(), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                 _logger.LogError(ex, "Error refreshing library");
            }

            return NoContent();
        }

        /// <summary>
        /// Deletes an item from the library and filesystem.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <response code="204">Item deleted.</response>
        /// <response code="401">Unauthorized access.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpDelete("/Items/{itemId}")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteItem(Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            var auth = _authContext.GetAuthorizationInfo(Request);
            var user = auth.User;

            if (!item.CanDelete(user))
            {
                return Unauthorized("Unauthorized access");
            }

            _libraryManager.DeleteItem(
                item,
                new DeleteOptions { DeleteFileLocation = true },
                true);

            return NoContent();
        }

        /// <summary>
        /// Deletes items from the library and filesystem.
        /// </summary>
        /// <param name="ids">The item ids.</param>
        /// <response code="204">Items deleted.</response>
        /// <response code="401">Unauthorized access.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpDelete("/Items")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status401Unauthorized)]
        public ActionResult DeleteItems([FromQuery] string? ids)
        {
            if (string.IsNullOrEmpty(ids))
            {
                return NoContent();
            }

            var itemIds = RequestHelpers.Split(ids, ',', true);
            foreach (var i in itemIds)
            {
                var item = _libraryManager.GetItemById(i);
                var auth = _authContext.GetAuthorizationInfo(Request);
                var user = auth.User;

                if (!item.CanDelete(user))
                {
                    if (ids.Length > 1)
                    {
                        return Unauthorized("Unauthorized access");
                    }

                    continue;
                }

                _libraryManager.DeleteItem(
                    item,
                    new DeleteOptions { DeleteFileLocation = true },
                    true);
            }

            return NoContent();
        }

        /// <summary>
        /// Get item counts.
        /// </summary>
        /// <param name="userId">Optional. Get counts from a specific user's library.</param>
        /// <param name="isFavorite">Optional. Get counts of favorite items.</param>
        /// <response code="200">Item counts returned.</response>
        /// <returns>Item counts.</returns>
        [HttpGet("/Items/Counts")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<ItemCounts> GetItemCounts(
            [FromQuery] Guid? userId,
            [FromQuery] bool? isFavorite)
        {
            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;

            var counts = new ItemCounts
            {
                AlbumCount = GetCount(typeof(MusicAlbum), user, isFavorite),
                EpisodeCount = GetCount(typeof(Episode), user, isFavorite),
                MovieCount = GetCount(typeof(Movie), user, isFavorite),
                SeriesCount = GetCount(typeof(Series), user, isFavorite),
                SongCount = GetCount(typeof(Audio), user, isFavorite),
                MusicVideoCount = GetCount(typeof(MusicVideo), user, isFavorite),
                BoxSetCount = GetCount(typeof(BoxSet), user, isFavorite),
                BookCount = GetCount(typeof(Book), user, isFavorite)
            };

            return counts;
        }

        /// <summary>
        /// Gets all parents of an item.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <response code="200">Item parents returned.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>Item parents.</returns>
        [HttpGet("/Items/{itemId}/Ancestors")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult<IEnumerable<BaseItemDto>> GetAncestors([FromRoute] Guid itemId, [FromQuery] Guid? userId)
        {
            var item = _libraryManager.GetItemById(itemId);

            if (item == null)
            {
                return NotFound("Item not found");
            }

            var baseItemDtos = new List<BaseItemDto>();

            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;

            var dtoOptions = new DtoOptions().AddClientFields(Request);
            BaseItem parent = item.GetParent();

            while (parent != null)
            {
                if (user != null)
                {
                    parent = TranslateParentItem(parent, user);
                }

                baseItemDtos.Add(_dtoService.GetBaseItemDto(parent, dtoOptions, user));

                parent = parent.GetParent();
            }

            return baseItemDtos;
        }

        /// <summary>
        /// Gets a list of physical paths from virtual folders.
        /// </summary>
        /// <response code="200">Physical paths returned.</response>
        /// <returns>List of physical paths.</returns>
        [HttpGet("/Library/PhysicalPaths")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<IEnumerable<string>> GetPhysicalPaths()
        {
            return Ok(_libraryManager.RootFolder.Children
                .SelectMany(c => c.PhysicalLocations));
        }

        /// <summary>
        /// Gets all user media folders.
        /// </summary>
        /// <param name="isHidden">Optional. Filter by folders that are marked hidden, or not.</param>
        /// <response code="200">Media folders returned.</response>
        /// <returns>List of user media folders.</returns>
        [HttpGet("/Library/MediaFolders")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<QueryResult<BaseItemDto>> GetMediaFolders([FromQuery] bool? isHidden)
        {
            var items = _libraryManager.GetUserRootFolder().Children.Concat(_libraryManager.RootFolder.VirtualChildren).OrderBy(i => i.SortName).ToList();

            if (isHidden.HasValue)
            {
                var val = isHidden.Value;

                items = items.Where(i => i.IsHidden == val).ToList();
            }

            var dtoOptions = new DtoOptions().AddClientFields(Request);
            var result = new QueryResult<BaseItemDto>
            {
                TotalRecordCount = items.Count,
                Items = items.Select(i => _dtoService.GetBaseItemDto(i, dtoOptions)).ToArray()
            };

            return result;
        }

        /// <summary>
        /// Reports that new episodes of a series have been added by an external source.
        /// </summary>
        /// <param name="tvdbId">The tvdbId.</param>
        /// <response code="204">Report success.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("/Library/Series/Added")]
        [HttpPost("/Library/Series/Updated")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult PostUpdatedSeries([FromQuery] string? tvdbId)
        {
            var series = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Series) },
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            }).Where(i => string.Equals(tvdbId, i.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tvdb), StringComparison.OrdinalIgnoreCase)).ToArray();

            foreach (var item in series)
            {
                _libraryMonitor.ReportFileSystemChanged(item.Path);
            }

            return NoContent();
        }

        /// <summary>
        /// Reports that new movies have been added by an external source.
        /// </summary>
        /// <param name="tmdbId">The tmdbId.</param>
        /// <param name="imdbId">The imdbId.</param>
        /// <response code="204">Report success.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("/Library/Movies/Added")]
        [HttpPost("/Library/Movies/Updated")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult PostUpdatedMovies([FromRoute] string? tmdbId, [FromRoute] string? imdbId)
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Movie) },
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            });

            if (!string.IsNullOrWhiteSpace(imdbId))
            {
                movies = movies.Where(i => string.Equals(imdbId, i.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Imdb), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else if (!string.IsNullOrWhiteSpace(tmdbId))
            {
                movies = movies.Where(i => string.Equals(tmdbId, i.GetProviderId(MediaBrowser.Model.Entities.MetadataProvider.Tmdb), StringComparison.OrdinalIgnoreCase)).ToList();
            }
            else
            {
                movies = new List<BaseItem>();
            }

            foreach (var item in movies)
            {
                _libraryMonitor.ReportFileSystemChanged(item.Path);
            }

            return NoContent();
        }

        /// <summary>
        /// Reports that new movies have been added by an external source.
        /// </summary>
        /// <param name="updates">A list of updated media paths.</param>
        /// <response code="204">Report success.</response>
        /// <returns>A <see cref="NoContentResult"/>.</returns>
        [HttpPost("/Library/Media/Updated")]
        [Authorize(Policy = Policies.DefaultAuthorization)]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        public ActionResult PostUpdatedMedia([FromBody, BindRequired] MediaUpdateInfoDto[] updates)
        {
            foreach (var item in updates)
            {
                _libraryMonitor.ReportFileSystemChanged(item.Path);
            }

            return NoContent();
        }

        /// <summary>
        /// Downloads item media.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <response code="200">Media downloaded.</response>
        /// <response code="404">Item not found.</response>
        /// <returns>A <see cref="FileResult"/> containing the media stream.</returns>
        /// <exception cref="ArgumentException">User can't download or item can't be downloaded.</exception>
        [HttpGet("/Items/{itemId}/Download")]
        [Authorize(Policy = Policies.Download)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public ActionResult GetDownload([FromRoute] Guid itemId)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null)
            {
                return NotFound();
            }

            var auth = _authContext.GetAuthorizationInfo(Request);

            var user = auth.User;

            if (user != null)
            {
                if (!item.CanDownload(user))
                {
                    throw new ArgumentException("Item does not support downloading");
                }
            }
            else
            {
                if (!item.CanDownload())
                {
                    throw new ArgumentException("Item does not support downloading");
                }
            }

            if (user != null)
            {
                LogDownload(item, user, auth);
            }

            var path = item.Path;

            // Quotes are valid in linux. They'll possibly cause issues here
            var filename = (Path.GetFileName(path) ?? string.Empty).Replace("\"", string.Empty, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(filename))
            {
                // Kestrel doesn't support non-ASCII characters in headers
                if (Regex.IsMatch(filename, @"[^\p{IsBasicLatin}]"))
                {
                    // Manually encoding non-ASCII characters, following https://tools.ietf.org/html/rfc5987#section-3.2.2
                    filename = WebUtility.UrlEncode(filename);
                }
            }

            // TODO determine non-ASCII validity.
            using var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read);
            return File(fileStream, MimeTypes.GetMimeType(path), filename);
        }

        /// <summary>
        /// Gets similar items.
        /// </summary>
        /// <param name="itemId">The item id.</param>
        /// <param name="excludeArtistIds">Exclude artist ids.</param>
        /// <param name="userId">Optional. Filter by user id, and attach user data.</param>
        /// <param name="limit">Optional. The maximum number of records to return.</param>
        /// <param name="fields">Optional. Specify additional fields of information to return in the output. This allows multiple, comma delimited. Options: Budget, Chapters, DateCreated, Genres, HomePageUrl, IndexOptions, MediaStreams, Overview, ParentId, Path, People, ProviderIds, PrimaryImageAspectRatio, Revenue, SortName, Studios, Taglines, TrailerUrls.</param>
        /// <response code="200">Similar items returned.</response>
        /// <returns>A <see cref="QueryResult{BaseItemDto}"/> containing the similar items.</returns>
        [HttpGet("/Artists/{itemId}/Similar")]
        [HttpGet("/Items/{itemId}/Similar")]
        [HttpGet("/Albums/{itemId}/Similar")]
        [HttpGet("/Shows/{itemId}/Similar")]
        [HttpGet("/Movies/{itemId}/Similar")]
        [HttpGet("/Trailers/{itemId}/Similar")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<QueryResult<BaseItemDto>> GetSimilarItems(
            [FromRoute] Guid itemId,
            [FromQuery] string? excludeArtistIds,
            [FromQuery] Guid? userId,
            [FromQuery] int? limit,
            [FromQuery] string? fields)
        {
            var item = itemId.Equals(Guid.Empty)
                ? (!userId.Equals(Guid.Empty)
                    ? _libraryManager.GetUserRootFolder()
                    : _libraryManager.RootFolder)
                : _libraryManager.GetItemById(itemId);

            var program = item as IHasProgramAttributes;
            var isMovie = item is MediaBrowser.Controller.Entities.Movies.Movie || (program != null && program.IsMovie) || item is Trailer;
            if (program != null && program.IsSeries)
            {
                return GetSimilarItemsResult(
                    item,
                    excludeArtistIds,
                    userId,
                    limit,
                    fields,
                    new[] { nameof(Series) },
                    false);
            }

            if (item is MediaBrowser.Controller.Entities.TV.Episode || (item is IItemByName && !(item is MusicArtist)))
            {
                return new QueryResult<BaseItemDto>();
            }

            return GetSimilarItemsResult(
                item,
                excludeArtistIds,
                userId,
                limit,
                fields,
                new[] { item.GetType().Name },
                isMovie);
        }

        /// <summary>
        /// Gets the library options info.
        /// </summary>
        /// <param name="libraryContentType">Library content type.</param>
        /// <param name="isNewLibrary">Whether this is a new library.</param>
        /// <response code="200">Library options info returned.</response>
        /// <returns>Library options info.</returns>
        [HttpGet("/Libraries/AvailableOptions")]
        [Authorize(Policy = Policies.FirstTimeSetupOrElevated)]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<LibraryOptionsResultDto> GetLibraryOptionsInfo(
            [FromQuery] string? libraryContentType,
            [FromQuery] bool isNewLibrary = false)
        {
            var result = new LibraryOptionsResultDto();

            var types = GetRepresentativeItemTypes(libraryContentType);
            var typesList = types.ToList();

            var plugins = _providerManager.GetAllMetadataPlugins()
                .Where(i => types.Contains(i.ItemType, StringComparer.OrdinalIgnoreCase))
                .OrderBy(i => typesList.IndexOf(i.ItemType))
                .ToList();

            result.MetadataSavers = plugins
                .SelectMany(i => i.Plugins.Where(p => p.Type == MetadataPluginType.MetadataSaver))
                .Select(i => new LibraryOptionInfoDto
                {
                    Name = i.Name,
                    DefaultEnabled = IsSaverEnabledByDefault(i.Name, types, isNewLibrary)
                })
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();

            result.MetadataReaders = plugins
                .SelectMany(i => i.Plugins.Where(p => p.Type == MetadataPluginType.LocalMetadataProvider))
                .Select(i => new LibraryOptionInfoDto
                {
                    Name = i.Name,
                    DefaultEnabled = true
                })
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();

            result.SubtitleFetchers = plugins
                .SelectMany(i => i.Plugins.Where(p => p.Type == MetadataPluginType.SubtitleFetcher))
                .Select(i => new LibraryOptionInfoDto
                {
                    Name = i.Name,
                    DefaultEnabled = true
                })
                .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.First())
                .ToArray();

            var typeOptions = new List<LibraryTypeOptionsDto>();

            foreach (var type in types)
            {
                TypeOptions.DefaultImageOptions.TryGetValue(type, out var defaultImageOptions);

                typeOptions.Add(new LibraryTypeOptionsDto
                {
                    Type = type,

                    MetadataFetchers = plugins
                    .Where(i => string.Equals(i.ItemType, type, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(i => i.Plugins.Where(p => p.Type == MetadataPluginType.MetadataFetcher))
                    .Select(i => new LibraryOptionInfoDto
                    {
                        Name = i.Name,
                        DefaultEnabled = IsMetadataFetcherEnabledByDefault(i.Name, type, isNewLibrary)
                    })
                    .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .ToArray(),

                    ImageFetchers = plugins
                    .Where(i => string.Equals(i.ItemType, type, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(i => i.Plugins.Where(p => p.Type == MetadataPluginType.ImageFetcher))
                    .Select(i => new LibraryOptionInfoDto
                    {
                        Name = i.Name,
                        DefaultEnabled = IsImageFetcherEnabledByDefault(i.Name, type, isNewLibrary)
                    })
                    .GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .ToArray(),

                    SupportedImageTypes = plugins
                    .Where(i => string.Equals(i.ItemType, type, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(i => i.SupportedImageTypes ?? Array.Empty<ImageType>())
                    .Distinct()
                    .ToArray(),

                    DefaultImageOptions = defaultImageOptions ?? Array.Empty<ImageOption>()
                });
            }

            result.TypeOptions = typeOptions.ToArray();

            return result;
        }

        private int GetCount(Type type, User? user, bool? isFavorite)
        {
            var query = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { type.Name },
                Limit = 0,
                Recursive = true,
                IsVirtualItem = false,
                IsFavorite = isFavorite,
                DtoOptions = new DtoOptions(false)
                {
                    EnableImages = false
                }
            };

            return _libraryManager.GetItemsResult(query).TotalRecordCount;
        }

        private BaseItem TranslateParentItem(BaseItem item, User user)
        {
            return item.GetParent() is AggregateFolder
                ? _libraryManager.GetUserRootFolder().GetChildren(user, true)
                    .FirstOrDefault(i => i.PhysicalLocations.Contains(item.Path))
                : item;
        }

        private void LogDownload(BaseItem item, User user, AuthorizationInfo auth)
        {
            try
            {
                _activityManager.Create(new ActivityLog(
                    string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("UserDownloadingItemWithValues"), user.Username, item.Name),
                    "UserDownloadingContent",
                    auth.UserId)
                {
                    ShortOverview = string.Format(CultureInfo.InvariantCulture, _localization.GetLocalizedString("AppDeviceValues"), auth.Client, auth.Device),
                });
            }
            catch
            {
                // Logged at lower levels
            }
        }

        private QueryResult<BaseItemDto> GetSimilarItemsResult(
            BaseItem item,
            string? excludeArtistIds,
            Guid? userId,
            int? limit,
            string? fields,
            string[] includeItemTypes,
            bool isMovie)
        {
            var user = userId.HasValue && !userId.Equals(Guid.Empty)
                ? _userManager.GetUserById(userId.Value)
                : null;
            var dtoOptions = new DtoOptions()
                .AddItemFields(fields)
                .AddClientFields(Request);

            var query = new InternalItemsQuery(user)
            {
                Limit = limit,
                IncludeItemTypes = includeItemTypes,
                IsMovie = isMovie,
                SimilarTo = item,
                DtoOptions = dtoOptions,
                EnableTotalRecordCount = !isMovie,
                EnableGroupByMetadataKey = isMovie
            };

            // ExcludeArtistIds
            if (!string.IsNullOrEmpty(excludeArtistIds))
            {
                query.ExcludeArtistIds = RequestHelpers.GetGuids(excludeArtistIds);
            }

            List<BaseItem> itemsResult;

            if (isMovie)
            {
                var itemTypes = new List<string> { nameof(MediaBrowser.Controller.Entities.Movies.Movie) };
                if (_serverConfigurationManager.Configuration.EnableExternalContentInSuggestions)
                {
                    itemTypes.Add(nameof(Trailer));
                    itemTypes.Add(nameof(LiveTvProgram));
                }

                query.IncludeItemTypes = itemTypes.ToArray();
                itemsResult = _libraryManager.GetArtists(query).Items.Select(i => i.Item1).ToList();
            }
            else if (item is MusicArtist)
            {
                query.IncludeItemTypes = Array.Empty<string>();

                itemsResult = _libraryManager.GetArtists(query).Items.Select(i => i.Item1).ToList();
            }
            else
            {
                itemsResult = _libraryManager.GetItemList(query);
            }

            var returnList = _dtoService.GetBaseItemDtos(itemsResult, dtoOptions, user);

            var result = new QueryResult<BaseItemDto>
            {
                Items = returnList,
                TotalRecordCount = itemsResult.Count
            };

            return result;
        }

        private static string[] GetRepresentativeItemTypes(string? contentType)
        {
            return contentType switch
            {
                CollectionType.BoxSets => new[] { "BoxSet" },
                CollectionType.Playlists => new[] { "Playlist" },
                CollectionType.Movies => new[] { "Movie" },
                CollectionType.TvShows => new[] { "Series", "Season", "Episode" },
                CollectionType.Books => new[] { "Book" },
                CollectionType.Music => new[] { "MusicArtist", "MusicAlbum", "Audio", "MusicVideo" },
                CollectionType.HomeVideos => new[] { "Video", "Photo" },
                CollectionType.Photos => new[] { "Video", "Photo" },
                CollectionType.MusicVideos => new[] { "MusicVideo" },
                _ => new[] { "Series", "Season", "Episode", "Movie" }
            };
        }

        private bool IsSaverEnabledByDefault(string name, string[] itemTypes, bool isNewLibrary)
        {
            if (isNewLibrary)
            {
                return false;
            }

            var metadataOptions = _serverConfigurationManager.Configuration.MetadataOptions
                .Where(i => itemTypes.Contains(i.ItemType ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            return metadataOptions.Length == 0 || metadataOptions.Any(i => !i.DisabledMetadataSavers.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        private bool IsMetadataFetcherEnabledByDefault(string name, string type, bool isNewLibrary)
        {
            if (isNewLibrary)
            {
                if (string.Equals(name, "TheMovieDb", StringComparison.OrdinalIgnoreCase))
                {
                    return !(string.Equals(type, "Season", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(type, "MusicVideo", StringComparison.OrdinalIgnoreCase));
                }

                return string.Equals(name, "TheTVDB", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "TheAudioDB", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(name, "MusicBrainz", StringComparison.OrdinalIgnoreCase);
            }

            var metadataOptions = _serverConfigurationManager.Configuration.MetadataOptions
                .Where(i => string.Equals(i.ItemType, type, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            return metadataOptions.Length == 0
               || metadataOptions.Any(i => !i.DisabledMetadataFetchers.Contains(name, StringComparer.OrdinalIgnoreCase));
        }

        private bool IsImageFetcherEnabledByDefault(string name, string type, bool isNewLibrary)
        {
            if (isNewLibrary)
            {
                if (string.Equals(name, "TheMovieDb", StringComparison.OrdinalIgnoreCase))
                {
                    return !string.Equals(type, "Series", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(type, "Season", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(type, "Episode", StringComparison.OrdinalIgnoreCase)
                           && !string.Equals(type, "MusicVideo", StringComparison.OrdinalIgnoreCase);
                }

                return string.Equals(name, "TheTVDB", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "Screen Grabber", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "TheAudioDB", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(name, "Image Extractor", StringComparison.OrdinalIgnoreCase);
            }

            var metadataOptions = _serverConfigurationManager.Configuration.MetadataOptions
                .Where(i => string.Equals(i.ItemType, type, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (metadataOptions.Length == 0)
            {
                return true;
            }

            return metadataOptions.Any(i => !i.DisabledImageFetchers.Contains(name, StringComparer.OrdinalIgnoreCase));
        }
    }
}