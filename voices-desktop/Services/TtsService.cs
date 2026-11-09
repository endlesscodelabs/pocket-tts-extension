using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using VoicesDesktop.Models;

namespace VoicesDesktop.Services;

public enum TtsStatus
{
    Idle,
    Synthesizing,
    Speaking,
    Error
}

public class TtsService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly MediaPlayer _mediaPlayer;
    private readonly string _serverBaseUrl;
    private CancellationTokenSource? _currentCts;
    private TtsStatus _status = TtsStatus.Idle;
    private string _lastSpokenText = string.Empty;
    private bool _disposed = false;

    public event EventHandler<TtsStatus>? StatusChanged;
    public event EventHandler<string>? ErrorOccurred;

    public TtsStatus Status
    {
        get => _status;
        private set
        {
            if (_status != value)
            {
                _status = value;
                StatusChanged?.Invoke(this, _status);
            }
        }
    }

    public string LastSpokenText => _lastSpokenText;
    public bool IsPlaying => _status == TtsStatus.Speaking;
    public bool IsActive => _status == TtsStatus.Speaking || _status == TtsStatus.Synthesizing;

    public TtsService(string serverBaseUrl = "http://127.0.0.1:8000")
    {
        _serverBaseUrl = serverBaseUrl.TrimEnd('/');
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(45)
        };

        _mediaPlayer = new MediaPlayer
        {
            AutoPlay = true
        };

        _mediaPlayer.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _mediaPlayer.MediaEnded += (s, e) =>
        {
            Status = TtsStatus.Idle;
        };
        _mediaPlayer.MediaFailed += (s, e) =>
        {
            Status = TtsStatus.Error;
            ErrorOccurred?.Invoke(this, e.ErrorMessage ?? "Media playback failed.");
        };
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession session, object args)
    {
        switch (session.PlaybackState)
        {
            case MediaPlaybackState.Playing:
                Status = TtsStatus.Speaking;
                break;
            case MediaPlaybackState.None:
                Status = TtsStatus.Idle;
                break;
            case MediaPlaybackState.Paused:
                if (session.NaturalDuration.TotalMilliseconds > 0 && 
                    session.Position >= session.NaturalDuration - TimeSpan.FromMilliseconds(150))
                {
                    Status = TtsStatus.Idle;
                }
                break;
        }
    }

    public async Task<bool> CheckHealthAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var response = await _httpClient.GetAsync($"{_serverBaseUrl}/health", cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task SpeakAsync(string text, string voice = "Alba", double speed = 1.0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();

        _currentCts = new CancellationTokenSource();
        var token = _currentCts.Token;

        _lastSpokenText = text;
        Status = TtsStatus.Synthesizing;

        var payload = new TtsRequest
        {
            Text = text,
            Voice = voice,
            Speed = speed
        };

        string json = JsonSerializer.Serialize(payload);

        // 1. Try Streaming Sentence Chunking Endpoint
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_serverBaseUrl}/tts/stream")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            if (response.IsSuccessStatusCode)
            {
                using var responseStream = await response.Content.ReadAsStreamAsync(token);
                using var reader = new StreamReader(responseStream, Encoding.UTF8);

                var playbackList = new MediaPlaybackList { AutoRepeatEnabled = false };
                bool started = false;

                playbackList.CurrentItemChanged += (s, e) =>
                {
                    if (e.NewItem == null && started)
                    {
                        Status = TtsStatus.Idle;
                    }
                };

                string? line;
                while ((line = await reader.ReadLineAsync(token)) != null)
                {
                    if (token.IsCancellationRequested) return;

                    line = line.Trim();
                    if (!line.StartsWith("data:")) continue;

                    string jsonStr = line.Substring(5).Trim();
                    if (string.IsNullOrEmpty(jsonStr)) continue;

                    try
                    {
                        using var doc = JsonDocument.Parse(jsonStr);
                        if (doc.RootElement.TryGetProperty("audio", out var audioProp))
                        {
                            string? b64 = audioProp.GetString();
                            if (!string.IsNullOrEmpty(b64))
                            {
                                byte[] audioBytes = Convert.FromBase64String(b64);
                                var inMemoryStream = new InMemoryRandomAccessStream();
                                using (var dataWriter = new DataWriter(inMemoryStream.GetOutputStreamAt(0)))
                                {
                                    dataWriter.WriteBytes(audioBytes);
                                    await dataWriter.StoreAsync();
                                    await dataWriter.FlushAsync();
                                }

                                var mediaSource = MediaSource.CreateFromStream(inMemoryStream, "audio/wav");
                                var playbackItem = new MediaPlaybackItem(mediaSource);
                                playbackList.Items.Add(playbackItem);

                                if (!started)
                                {
                                    started = true;
                                    _mediaPlayer.Source = playbackList;
                                    _mediaPlayer.PlaybackSession.PlaybackRate = speed;
                                    _mediaPlayer.Play();
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (started) return;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            // Fallback to standard /tts below
        }

        // 2. Standard Endpoint Fallback
        try
        {
            using var fallbackContent = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"{_serverBaseUrl}/tts", fallbackContent, token);

            if (!response.IsSuccessStatusCode)
            {
                string errorBody = await response.Content.ReadAsStringAsync(token);
                Status = TtsStatus.Error;
                ErrorOccurred?.Invoke(this, $"TTS server error ({response.StatusCode}): {errorBody}");
                return;
            }

            byte[] audioBytes = await response.Content.ReadAsByteArrayAsync(token);
            if (audioBytes.Length == 0)
            {
                Status = TtsStatus.Error;
                ErrorOccurred?.Invoke(this, "Empty audio stream returned from TTS server.");
                return;
            }

            var inMemoryStream = new InMemoryRandomAccessStream();
            using (var dataWriter = new DataWriter(inMemoryStream.GetOutputStreamAt(0)))
            {
                dataWriter.WriteBytes(audioBytes);
                await dataWriter.StoreAsync();
                await dataWriter.FlushAsync();
            }

            var mediaSource = MediaSource.CreateFromStream(inMemoryStream, "audio/wav");
            _mediaPlayer.Source = mediaSource;
            _mediaPlayer.PlaybackSession.PlaybackRate = speed;
            _mediaPlayer.Play();
        }
        catch (OperationCanceledException)
        {
            // User aborted playback
        }
        catch (Exception ex)
        {
            Status = TtsStatus.Error;
            ErrorOccurred?.Invoke(this, ex.Message);
        }
    }

    public void Stop()
    {
        try
        {
            if (_currentCts != null)
            {
                _currentCts.Cancel();
                _currentCts.Dispose();
                _currentCts = null;
            }
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
        }
        catch
        {
            // Ignore teardown exceptions
        }
        finally
        {
            Status = TtsStatus.Idle;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _currentCts?.Cancel();
            _currentCts?.Dispose();
            _mediaPlayer.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
            _mediaPlayer.Dispose();
            _httpClient.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
