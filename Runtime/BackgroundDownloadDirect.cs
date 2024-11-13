using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace Unity.Networking
{
    public sealed class BackgroundDownloadDirect : BackgroundDownload
    {
        private UnityWebRequest         m_Request;
        private CancellationTokenSource m_Cts;

        public BackgroundDownloadDirect(BackgroundDownloadConfig config) : base(config)
        {
            m_Cts = new CancellationTokenSource();
            StartDownloadAsync(m_Cts.Token);
        }

        public override bool keepWaiting => _status == BackgroundDownloadStatus.Downloading;

        protected override float GetProgress()
        {
            return m_Request?.downloadProgress ?? 1.0f;
        }

        private async void StartDownloadAsync(CancellationToken cancellationToken)
        {
            try
            {
                using (m_Request = UnityWebRequest.Get(config.url))
                {
                    config.requestHeaders?.Where(h => h.Value != null)
                          .ToList()
                          .ForEach(h => h.Value.ForEach(v => m_Request.SetRequestHeader(h.Key, v)));

                    try
                    {
                        await m_Request.SendWebRequest().WithCancellation(cancellationToken);
                    }
                    catch (Exception e) when (e is UnityWebRequestException or OperationCanceledException)
                    {
                        _status = BackgroundDownloadStatus.Failed;
                        _error = e.Message;
                        return;
                    }

                    if (m_Request.result == UnityWebRequest.Result.Success)
                    {
                        string filePath = Path.Combine(Application.persistentDataPath, config.filePath);
                        string dir = Path.GetDirectoryName(filePath);
                        if (!Directory.Exists(dir))
                            Directory.CreateDirectory(dir);
                        try
                        {
                            await File.WriteAllBytesAsync(filePath, m_Request.downloadHandler.data);
                            _status = BackgroundDownloadStatus.Done;
                        }
                        catch (Exception e)
                        {
                            _status = BackgroundDownloadStatus.Failed;
                            _error = e.Message;
                            return;
                        }
                    }
                    else
                    {
                        _status = BackgroundDownloadStatus.Failed;
                        _error = m_Request.error;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                _status = BackgroundDownloadStatus.Failed;
                _error = e.Message;
                Debug.unityLogger.LogError(nameof(BackgroundDownloadDirect), $"An unexpected error occured while downloading '{config.url}': {e.Message}");
                return;
            }
        }

        internal static Dictionary<string, BackgroundDownload> LoadDownloads()
        {
            return new Dictionary<string, BackgroundDownload>();
        }

        internal static void SaveDownloads(Dictionary<string, BackgroundDownload> downloads)
        {
            // do nothing
        }

        public override void Dispose()
        {
            m_Cts?.Cancel();
            m_Cts?.Dispose();
            m_Cts = null;
            base.Dispose();
        }
    }
}
