using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Soundy.Backend.Models;

namespace Soundy.Backend.Services;

public interface ITrackService
{
    Task InitializeAsync();
    Task<Track?> GetTrackAsync(string trackId);
    Task<List<Track>> GetAllTracksAsync(int limit, int offset);
    Task<List<Track>> SearchTracksAsync(string? query, int limit, int offset);
    Task<Track> UploadTrackAsync(IFormFile audioFile, CreateTrackRequest? metadata, IFormFile? coverFile);
    Task DeleteTrackAsync(string trackId);
    Task<int> ScanDirectoryAsync();
    Task IncrementPlayCountAsync(string trackId);
}
