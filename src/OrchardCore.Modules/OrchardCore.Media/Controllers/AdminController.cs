using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using OrchardCore.FileStorage;
using OrchardCore.Media.Services;

namespace OrchardCore.Media.Controllers
{
    public class AdminController : Controller
    {
        private readonly IMediaFileStore _mediaFileStore;
        private readonly IAuthorizationService _authorizationService;
        private readonly IContentTypeProvider _contentTypeProvider;
        private readonly ILogger _logger;
        private readonly IStringLocalizer<AdminController> T;

        public AdminController(
            IMediaFileStore mediaFileStore,
            IAuthorizationService authorizationService,
            IContentTypeProvider contentTypeProvider,
            ILogger<AdminController> logger,
            IStringLocalizer<AdminController> stringLocalizer)
        {
            _mediaFileStore = mediaFileStore;
            _authorizationService = authorizationService;
            _contentTypeProvider = contentTypeProvider;
            _logger = logger;
            T = stringLocalizer;

        }

        public async Task<IActionResult> Index()
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia))
            {
                return Unauthorized();
            }

            return View();
        }

        public async Task<ActionResult<IEnumerable<IFileStoreEntry>>> GetFolders(string path)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia))
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(path))
            {
                path = "";
            }

            if (await _mediaFileStore.GetDirectoryInfoAsync(path) == null)
            {
                return NotFound();
            }


            var content = (await _mediaFileStore.GetDirectoryContentAsync(path)).Where(x => x.IsDirectory);

            var filtered = new List<IFileStoreEntry>();

            foreach (var entry in content)
            {
                if ((await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)entry.Path)))
                {
                    filtered.Add(entry);
                }
            }

            return filtered.ToArray();
        }


        public async Task<ActionResult<IEnumerable<object>>> GetMediaItems(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = "";
            }

            
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)path))
            {
                return Unauthorized();
            }

            if (await _mediaFileStore.GetDirectoryInfoAsync(path) == null)
            {
                return NotFound();
            }

            var dirs = (await _mediaFileStore.GetDirectoryContentAsync(path)).Where(x => !x.IsDirectory);

            var filtered = new List<IFileStoreEntry>();
            foreach (var entry in dirs)
            {
                if ((await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)entry.Path)))
                {
                    filtered.Add(entry);
                }
            }

            return filtered.Select(CreateFileResult).ToArray();
        }

        public async Task<ActionResult<object>> GetMediaItem(string path)
        {
            // Don't require Permission.ManageMediaFieldsFolder here. 
            // It will break the limited media field editor for user without that permission.
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)) 
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(path))
            {
                return NotFound();
            }

            var f = await _mediaFileStore.GetFileInfoAsync(path);

            if (f == null)
            {
                return NotFound();
            }

            return CreateFileResult(f);
        }

        [HttpPost]
        public async Task<ActionResult<object>> Upload(
            string path,
            string contentType,
            ICollection<IFormFile> files)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia))
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(path))
            {
                path = "";
            }

            var result = new List<object>();

            // TODO: Validate file extensions

            // Loop through each file in the request
            foreach (var file in files)
            {
                // TODO: support clipboard

                try
                {
                    var mediaFilePath = _mediaFileStore.Combine(path, file.FileName);

                    using (var stream = file.OpenReadStream())
                    {
                        await _mediaFileStore.CreateFileFromStream(mediaFilePath, stream);
                    }

                    var mediaFile = await _mediaFileStore.GetFileInfoAsync(mediaFilePath);

                    result.Add(CreateFileResult(mediaFile));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "An error occured while uploading a media");

                    result.Add(new
                    {
                        name = file.FileName,
                        size = file.Length,
                        folder = path,
                        error = ex.Message
                    });
                }
            }

            return new { files = result.ToArray() };
        }

        [HttpPost]
        public async Task<IActionResult> DeleteFolder(string path)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)path))
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(path))
            {
                return StatusCode(StatusCodes.Status403Forbidden, T["Cannot delete root media folder"]);
            }

            var mediaFolder = await _mediaFileStore.GetDirectoryInfoAsync(path);
            if (mediaFolder != null && !mediaFolder.IsDirectory)
            {
                return StatusCode(StatusCodes.Status403Forbidden, T["Cannot delete path because it is not a directory"]);
            }

            if (await _mediaFileStore.TryDeleteDirectoryAsync(path) == false)
                return NotFound();

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> DeleteMedia(string path)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)path))
            {
                return Unauthorized();
            }

            if (string.IsNullOrEmpty(path))
            {
                return NotFound();
            }

            if (await _mediaFileStore.TryDeleteFileAsync(path) == false)
                return NotFound();

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> MoveMedia(string oldPath, string newPath)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)oldPath)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)newPath))
            {
                return Unauthorized();
            }


            if (string.IsNullOrEmpty(oldPath) || string.IsNullOrEmpty(newPath))
            {
                return NotFound();
            }

            if (await _mediaFileStore.GetFileInfoAsync(oldPath) == null)
            {
                return NotFound();
            }

            if (await _mediaFileStore.GetFileInfoAsync(newPath) != null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, T["Cannot move media because a file already exists with the same name"]);
            }

            await _mediaFileStore.MoveFileAsync(oldPath, newPath);

            return Ok();
        }

        [HttpPost]
        public async Task<IActionResult> DeleteMediaList(string[] paths)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia))
            {
                return Unauthorized();
            }

            foreach (var path in paths)
            {
                if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object) path))
                {
                    return Unauthorized();
                }
            }

            if (paths == null)
            {
                return NotFound();
            }

            foreach (var p in paths)
            {
                if (await _mediaFileStore.TryDeleteFileAsync(p) == false)
                    return NotFound();
            }

            return Ok();
        }



        [HttpPost]
        public async Task<IActionResult> MoveMediaList(string[] mediaNames, string sourceFolder, string targetFolder)
        {
            if (!await _authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)sourceFolder)
                || !await _authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)targetFolder))
            {
                return Unauthorized();
            }

            if ((mediaNames == null) || (mediaNames.Length < 1)
                || string.IsNullOrEmpty(sourceFolder)
                || string.IsNullOrEmpty(targetFolder))
            {
                return NotFound();
            }

            sourceFolder = sourceFolder == "root" ? string.Empty : sourceFolder;
            targetFolder = targetFolder == "root" ? string.Empty : targetFolder;

            var filesOnError = new List<string>();

            foreach (var name in mediaNames)
            {
                var sourcePath = _mediaFileStore.Combine(sourceFolder, name);
                var targetPath = _mediaFileStore.Combine(targetFolder, name);
                try
                {
                    await _mediaFileStore.MoveFileAsync(sourcePath, targetPath);
                }
                catch (FileStoreException)
                {
                    filesOnError.Add(sourcePath);
                }
            }

            if (filesOnError.Count > 0)
            {
                return BadRequest(T["Error when moving files. Maybe they already exist on the target folder? Files on error: {0}", string.Join(",", filesOnError)].ToString());
            }
            else
            {
                return Ok();
            }
        }

        [HttpPost]
        public async Task<ActionResult<IFileStoreEntry>> CreateFolder(
            string path, string name,
            [FromServices] IAuthorizationService authorizationService)
        {
            if (string.IsNullOrEmpty(path))
            {
                path = "";
            }

            var newPath = _mediaFileStore.Combine(path, name);

            if (!await authorizationService.AuthorizeAsync(User, Permissions.ManageOwnMedia)
                || !await authorizationService.AuthorizeAsync(User, Permissions.ManageMediaFieldsFolder, (object)newPath))
            {
                return Unauthorized();
            }

            var mediaFolder = await _mediaFileStore.GetDirectoryInfoAsync(newPath);
            if (mediaFolder != null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, T["Cannot create folder because a folder already exists with the same name"]);
            }

            var existingFile = await _mediaFileStore.GetFileInfoAsync(newPath);
            if (existingFile != null)
            {
                return StatusCode(StatusCodes.Status403Forbidden, T["Cannot create folder because a file already exists with the same name"]);
            }

            await _mediaFileStore.TryCreateDirectoryAsync(newPath);

            mediaFolder = await _mediaFileStore.GetDirectoryInfoAsync(newPath);

            return new ObjectResult(mediaFolder);
        }

        public IActionResult MediaApplication()
        {
            return View();
        }

        public object CreateFileResult(IFileStoreEntry mediaFile)
        {
            _contentTypeProvider.TryGetContentType(mediaFile.Name, out var contentType);

            return new
            {
                name = mediaFile.Name,
                size = mediaFile.Length,
                folder = mediaFile.DirectoryPath,
                url = _mediaFileStore.MapPathToPublicUrl(mediaFile.Path),
                mediaPath = mediaFile.Path,
                mime = contentType ?? "application/octet-stream"
            };
        }
    }
}
