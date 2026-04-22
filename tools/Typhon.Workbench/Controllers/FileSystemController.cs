using Microsoft.AspNetCore.Mvc;
using Typhon.Workbench.Dtos.Fs;
using Typhon.Workbench.Fs;
using Typhon.Workbench.Middleware;

namespace Typhon.Workbench.Controllers;

[ApiController]
[Route("api/fs")]
[Tags("Files")]
[RequireBootstrapToken]
public sealed class FileSystemController : ControllerBase
{
    private readonly FileBrowserService _browser;

    public FileSystemController(FileBrowserService browser) => _browser = browser;

    [HttpGet("home")]
    public ActionResult<FileEntryDto> Home()
    {
        var path = _browser.Home();
        return Ok(_browser.Stat(path));
    }

    [HttpGet("list")]
    public ActionResult<DirectoryListingDto> List([FromQuery] string path) => Ok(_browser.List(path));

    [HttpGet("stat")]
    public ActionResult<FileEntryDto> Stat([FromQuery] string path) => Ok(_browser.Stat(path));
}
