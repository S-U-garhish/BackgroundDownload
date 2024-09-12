#if UNITY_ANDROID

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Unity.Networking
{
    public class AsyncLock
    {
        private readonly SemaphoreSlim _SemaphoreSlim = new(1,1);
        public Task WaitAsync() => _SemaphoreSlim.WaitAsync();
        public void Release() => _SemaphoreSlim.Release();
    }
    
    class BackgroundDownloadAndroid : BackgroundDownload
    {
        private const string TEMP_FILE_SUFFIX = ".part";
        static AndroidJavaClass _playerClass;
        static AndroidJavaClass _backgroundDownloadClass;
        private CancellationTokenSource _CancellationTokenSource;
        private AndroidJavaObject _download;
        private long _id = 0;
        private string _tempFilePath;
        private bool _started;
        
        static AndroidJavaObject _currentActivity;
        static Callback _finishedCallback;
        static AsyncLock _asyncLock = new ();
        
        class Callback : AndroidJavaProxy
        {
            public Callback()
                : base("com.unity3d.backgrounddownload.CompletionReceiver$Callback")
            {}

            void downloadCompleted()
            {
                lock (typeof(BackgroundDownload))
                {
                    foreach (var download in _downloads.Values)
                        ((BackgroundDownloadAndroid) download).CheckFinished();
                }
            }
        }
        
        static void SetupBackendStatics()
        {
            if (_backgroundDownloadClass == null)
                _backgroundDownloadClass = new AndroidJavaClass("com.unity3d.backgrounddownload.BackgroundDownload");
            if (_finishedCallback == null)
            {
                _finishedCallback = new Callback();
                var receiver = new AndroidJavaClass("com.unity3d.backgrounddownload.CompletionReceiver");
                receiver.CallStatic("setCallback", _finishedCallback);
            }
            if (_playerClass == null)
                _playerClass = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            
            if (_currentActivity == null)
            {
                _currentActivity = _playerClass.GetStatic<AndroidJavaObject>("currentActivity");
            }
        }

        internal BackgroundDownloadAndroid(BackgroundDownloadConfig config)
            : base(config)
        {
            SetupBackendStatics();
            _CancellationTokenSource = new CancellationTokenSource();
            StartDownloadAsync(_CancellationTokenSource.Token);
        }
        
        BackgroundDownloadAndroid(long id, AndroidJavaObject download)
        {
            _id = id;
            _download = download;
            _config.url = QueryDownloadUri();
            _config.filePath = QueryDestinationPath(out _tempFilePath);
            
            _started = true;
            CheckFinished();
        }
        
        private async void StartDownloadAsync(CancellationToken cancellationToken)
        {
            await _asyncLock.WaitAsync();
            try
            {
                string filePath = Path.Combine(Application.persistentDataPath, config.filePath);
                
                // Running RunDownloadTask on thread pool as it is expensive operation 
                await Task.Run(async () =>
                {
                    AndroidJNI.AttachCurrentThread();
                    await RunDownloadTask(filePath);
                    AndroidJNI.DetachCurrentThread();
                }, cancellationToken); 
            }
            finally
            {
                _asyncLock.Release();
            }
        }
        
        private Task RunDownloadTask(string filePath)
        {
            _tempFilePath = filePath + TEMP_FILE_SUFFIX;
            
            if (File.Exists(filePath))
                File.Delete(filePath);
            
            if (File.Exists(_tempFilePath))
                File.Delete(_tempFilePath);
            else
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            string fileUri = "file://" + _tempFilePath;
            bool allowMetered = false;
            bool allowRoaming = false;
            
            switch (_config.policy)
            {
                case BackgroundDownloadPolicy.AllowMetered:
                    allowMetered = true;
                    break;
                case BackgroundDownloadPolicy.AlwaysAllow:
                    allowMetered = true;
                    allowRoaming = true;
                    break;
                default:
                    break;
            }

            _download = _backgroundDownloadClass.CallStatic<AndroidJavaObject>("create", config.url.AbsoluteUri,
                fileUri);
                
            _download.Call("setAllowMetered", allowMetered);
            _download.Call("setAllowRoaming", allowRoaming);
            
            if (config.requestHeaders != null)
                foreach (var header in config.requestHeaders)
                    if (header.Value != null)
                        foreach (var val in header.Value)
                            _download.Call("addRequestHeader", header.Key, val);

            _id = _download.Call<long>("start", _currentActivity);
            _started = true;
            return Task.CompletedTask;
        }
        
      
        static BackgroundDownloadAndroid Recreate(long id)
        {
            try
            {
                SetupBackendStatics();
              
                var download = _backgroundDownloadClass.CallStatic<AndroidJavaObject>("recreate", 
                    _currentActivity, id);
                
                if (download != null)
                    return new BackgroundDownloadAndroid(id, download);
            }
            catch (Exception e)
            {
                Debug.LogError(string.Format("Failed to recreate background download with id {0}: {1}", id, e.Message));
            }

            return null;
        }

        Uri QueryDownloadUri()
        {
            return new Uri(_download.Call<string>("getDownloadUrl"));
        }

        string QueryDestinationPath(out string tempFilePath)
        {
            string uri = _download.Call<string>("getDestinationUri");
            string basePath = Application.persistentDataPath;
            var pos = uri.IndexOf(basePath);
            tempFilePath = uri.Substring(pos);
            pos += basePath.Length;
            if (uri[pos] == '/')
                ++pos;
            var suffixPos = uri.LastIndexOf(TEMP_FILE_SUFFIX);
            if (suffixPos > 0)
            {
                var length = suffixPos;
                length -= pos;
                return uri.Substring(pos, length);
            }

            return uri.Substring(pos);
        }

        string GetError()
        {
            return _download.Call<string>("getError");
        }

        void CheckFinished()
        {
            if (_status == BackgroundDownloadStatus.Downloading)
            {
                CheckFinishedAsync();
            }
        }

        async void CheckFinishedAsync()
        {
            await _asyncLock.WaitAsync();
            try
            {
                await Task.Run(() =>
                {
                    AndroidJNI.AttachCurrentThread();
                    int finishStatus = _download.Call<int>("checkFinished");
                    if (finishStatus == 1)
                    {
                        if (_tempFilePath.EndsWith(TEMP_FILE_SUFFIX))
                        {
                            string filePath =
                                _tempFilePath.Substring(0, _tempFilePath.Length - TEMP_FILE_SUFFIX.Length);
                            if (File.Exists(_tempFilePath))
                            {
                                if (File.Exists(filePath))
                                    File.Delete(filePath);
                                File.Move(_tempFilePath, filePath);
                            }
                        }

                        _status = BackgroundDownloadStatus.Done;
                    }
                    else if (finishStatus < 0)
                    {
                        _status = BackgroundDownloadStatus.Failed;
                        _error = GetError();
                    }
                    AndroidJNI.DetachCurrentThread();
                });
            }
            finally
            {
                _asyncLock.Release();  
            }
        }
        
        void RemoveDownload()
        {
            RemoveDownloadAsync();
        }
        
        async void RemoveDownloadAsync()
        {
            await _asyncLock.WaitAsync();
            try
            {
                // Calling "remove" on thread pool which is expensive operation
                await Task.Run(() =>
                {
                    AndroidJNI.AttachCurrentThread();
                    _download.Call("remove");
                    AndroidJNI.DetachCurrentThread();
                });   
            }
            finally
            {
                _asyncLock.Release();  
            }
        }
        
        public override bool keepWaiting { get { return _status == BackgroundDownloadStatus.Downloading; } }

        protected override float GetProgress()
        {
            return _download.Call<float>("getProgress");
        }

        public override void Dispose()
        {
            _CancellationTokenSource?.Cancel();
            _CancellationTokenSource?.Dispose();
            _CancellationTokenSource = null;
            
            RemoveDownload();
            base.Dispose();
        }

        internal static Dictionary<string, BackgroundDownload> LoadDownloads()
        {
            var downloads = new Dictionary<string, BackgroundDownload>();
            var file = Path.Combine(Application.persistentDataPath, "unity_background_downloads.dl");
            if (File.Exists(file))
            {
                foreach (var line in File.ReadAllLines(file))
                    if (!string.IsNullOrEmpty(line))
                    {
                        long id = long.Parse(line);
                        var dl = Recreate(id);
                        if (dl != null)
                            downloads[dl.config.filePath] = dl;
                    }
            }

            // some loads might have failed, save the actual state
            SaveDownloads(downloads);
            return downloads;
        }

        internal static void SaveDownloads(Dictionary<string, BackgroundDownload> downloads)
        {
            var file = Path.Combine(Application.persistentDataPath, "unity_background_downloads.dl");
            if (downloads.Count > 0)
            {
                var ids = new string[downloads.Count];
                int i = 0;
                foreach (var dl in downloads)
                    ids[i++] = ((BackgroundDownloadAndroid)dl.Value)._id.ToString();
                File.WriteAllLines(file, ids);
            }
            else if (File.Exists(file))
                File.Delete(file);
        }
    }
}
#endif
