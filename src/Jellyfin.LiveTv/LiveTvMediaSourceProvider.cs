#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.LiveTv
{
    public class LiveTvMediaSourceProvider : IMediaSourceProvider
    {
        // Do not use a pipe here because Roku http requests to the server will fail, without any explicit error message.
        private const char StreamIdDelimiter = '_';

        private readonly ILiveTvManager _liveTvManager;
        private readonly IRecordingsManager _recordingsManager;
        private readonly ILogger<LiveTvMediaSourceProvider> _logger;
        private readonly IMediaSourceManager _mediaSourceManager;
        private readonly IServerApplicationHost _appHost;

        public LiveTvMediaSourceProvider(ILiveTvManager liveTvManager, IRecordingsManager recordingsManager, ILogger<LiveTvMediaSourceProvider> logger, IMediaSourceManager mediaSourceManager, IServerApplicationHost appHost)
        {
            _liveTvManager = liveTvManager;
            _recordingsManager = recordingsManager;
            _logger = logger;
            _mediaSourceManager = mediaSourceManager;
            _appHost = appHost;
        }

        public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
        {
            if (item.SourceType == SourceType.LiveTV)
            {
                var activeRecordingInfo = _recordingsManager.GetActiveRecordingInfo(item.Path);

                if (string.IsNullOrEmpty(item.Path) || activeRecordingInfo is not null)
                {
                    return GetMediaSourcesInternal(item, activeRecordingInfo, cancellationToken);
                }
            }

            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        private async Task<IEnumerable<MediaSourceInfo>> GetMediaSourcesInternal(BaseItem item, ActiveRecordingInfo activeRecordingInfo, CancellationToken cancellationToken)
        {
            IEnumerable<MediaSourceInfo> sources;

            var forceRequireOpening = false;

            try
            {
                if (activeRecordingInfo is not null)
                {
                    sources = await _mediaSourceManager.GetRecordingStreamMediaSources(activeRecordingInfo, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    sources = await _liveTvManager.GetChannelMediaSources(item, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (NotImplementedException)
            {
                sources = _mediaSourceManager.GetStaticMediaSources(item, false);

                forceRequireOpening = true;
            }

            var list = sources.ToList();

            foreach (var source in list)
            {
                source.Type = MediaSourceType.Default;
                source.BufferMs ??= 1500;

                if (source.RequiresOpening || forceRequireOpening)
                {
                    source.RequiresOpening = true;
                }

                if (source.RequiresOpening)
                {
                    var openKeys = new List<string>
                    {
                        item.GetType().Name,
                        item.Id.ToString("N", CultureInfo.InvariantCulture),
                        source.Id ?? string.Empty
                    };

                    source.OpenToken = string.Join(StreamIdDelimiter, openKeys);
                }

                // Dummy this up so that direct play checks can still run
                if (string.IsNullOrEmpty(source.Path) && source.Protocol == MediaProtocol.Http)
                {
                    source.Path = _appHost.GetApiUrlForLocalAccess();
                }
            }

            _logger.LogDebug("MediaSources: {@MediaSources}", list);

            return list;
        }

        /// <inheritdoc />
        public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
        {
            var keys = openToken.Split(StreamIdDelimiter, 3);
            var mediaSourceId = keys.Length >= 3 ? keys[2] : null;

            var info = await _liveTvManager.GetChannelStream(keys[1], mediaSourceId, currentLiveStreams, cancellationToken).ConfigureAwait(false);
            var liveStream = info.Item2;

            return liveStream;
        }
    }
}
