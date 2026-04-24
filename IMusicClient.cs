using System.Threading;
using System.Threading.Tasks;

namespace WKMusic;

public interface IMusicClient
{
    Task InitializeAsync(CancellationToken ct = default);
    Task<PlaybackState> GetPlaybackStateAsync(CancellationToken ct = default);
}