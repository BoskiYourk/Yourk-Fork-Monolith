// SPDX-FileCopyrightText: 2025 Pieter-Jan Briers
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Content.Client.Parallax.Data;
using Content.Shared.CCVar;
using Nett;
using Robust.Client.Graphics;
using Robust.Shared.Collections;
using Robust.Shared.Configuration;
using Robust.Shared.ContentPack;
using Robust.Shared.Utility;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Content.Client.Parallax.Managers;

/// <summary>
/// Caches the textures generated by <see cref="GeneratedParallaxTextureSource"/>
/// </summary>
public sealed class GeneratedParallaxCache : IPostInjectInit
{
    [Dependency] private readonly IConfigurationManager _cfg = null!;
    [Dependency] private readonly IResourceManager _res = null!;
    [Dependency] private readonly ILogManager _logManager = null!;

    private readonly Dictionary<string, CacheDatum> _data = new();

    private ISawmill _sawmill = null!;

    public Task<Texture> Load(string id, ResPath configPath, CancellationToken cancel = default)
    {
        if (!_data.TryGetValue(id, out var datum))
        {
            _sawmill.Verbose($"Loading new generated layer {id} with config path {configPath}");

            var cts = new CancellationTokenSource();

            var loadTask = LoadTask(id, configPath, cts.Token);
            datum = new CacheDatum
            {
                CancellationSource = cts,
                ConfigPath = configPath,
                LoadTask = loadTask,
            };

            _data.Add(id, datum);
        }
        else
        {
            if (datum.ConfigPath != configPath)
                throw new InvalidOperationException("Generated parallax layers with the same ID must have the same config path!");
        }

        datum.RefCount += 1;

        if (!datum.LoadTask.IsCompleted)
            cancel.Register(() => Unload(id));

        return datum.LoadTask;
    }

    public void Unload(string id)
    {
        if (!_data.TryGetValue(id, out var datum))
            throw new InvalidOperationException("Layer is not cached!");

        DebugTools.Assert(datum.RefCount >= 1);

        datum.RefCount -= 1;
        if (datum.RefCount == 0)
        {
            _sawmill.Verbose($"Unloading generated layer {id}");

            // If we're still loading, cancel the active load.
            datum.CancellationSource.Cancel();

            // We should probably be unloading the texture here forcibly,
            // but the previous code didn't so I won't either.
            _data.Remove(id);
        }
    }

    private async Task<Texture> LoadTask(string id, ResPath configPath, CancellationToken cancel)
    {
        return await GenerateTexture(id, configPath, cancel);
    }

    private async Task<Texture> GenerateTexture(string id, ResPath configPath, CancellationToken cancel)
    {
        var parallaxConfig = GetParallaxConfig(configPath);
        if (parallaxConfig == null)
        {
            _sawmill.Error($"Parallax config not found or unreadable: {configPath}");
            // The show must go on.
            return Texture.Transparent;
        }

        var debugParallax = _cfg.GetCVar(CCVars.ParallaxDebug);

        if (debugParallax
            || !_res.UserData.TryReadAllText(PreviousConfigPath(id), out var previousParallaxConfig)
            || previousParallaxConfig != parallaxConfig)
        {
            var table = Toml.ReadString(parallaxConfig);
            await UpdateCachedTexture(id, table, debugParallax, cancel);

            //Update the previous config
            using var writer = _res.UserData.OpenWriteText(PreviousConfigPath(id));
            writer.Write(parallaxConfig);
        }

        try
        {
            return GetCachedTexture(id);
        }
        catch (Exception ex)
        {
            _sawmill.Error($"Couldn't retrieve parallax cached texture: {ex}");

            try
            {
                // Also try to at least sort of fix this if we've been fooled by a config backup
                _res.UserData.Delete(PreviousConfigPath(id));
            }
            catch (Exception)
            {
                // The show must go on.
            }

            return Texture.Transparent;
        }
    }

    private async Task UpdateCachedTexture(string id, TomlTable config, bool saveDebugLayers, CancellationToken cancel)
    {
        var debugImages = saveDebugLayers ? new List<Image<Rgba32>>() : null;

        // Generate the parallax in the thread pool.
        using var newParallexImage = await Task.Run(() =>
                ParallaxGenerator.GenerateParallax(config, new Size(1920, 1080), _sawmill, debugImages, cancel),
            cancel);

        // And load it in the main thread for safety reasons.
        // But before spending time saving it, make sure to exit out early if it's not wanted.
        cancel.ThrowIfCancellationRequested();

        // Store it and CRC so further game starts don't need to regenerate it.
        await using var imageStream = _res.UserData.OpenWrite(CachedImagePath(id));
        await newParallexImage.SaveAsPngAsync(imageStream, cancel);

        if (saveDebugLayers)
        {
            for (var i = 0; i < debugImages!.Count; i++)
            {
                var debugImage = debugImages[i];
                await using var debugImageStream =
                    _res.UserData.OpenWrite(new ResPath($"/parallax_{id}debug_{i}.png"));
                await debugImage.SaveAsPngAsync(debugImageStream, cancel);
            }
        }
    }

    private Texture GetCachedTexture(string id)
    {
        using var imageStream = _res.UserData.OpenRead(CachedImagePath(id));
        return Texture.LoadFromPNGStream(imageStream, $"Parallax {id}");
    }

    private string? GetParallaxConfig(ResPath configPath)
    {
        if (!_res.TryContentFileRead(configPath, out var configStream))
            return null;

        using var configReader = new StreamReader(configStream, EncodingHelpers.UTF8);
        return configReader.ReadToEnd().Replace(Environment.NewLine, "\n");
    }

    private static ResPath CachedImagePath(string identifier)
    {
        return new ResPath($"/parallax_{identifier}cache.png");
    }

    private static ResPath PreviousConfigPath(string identifier)
    {
        return new ResPath($"/parallax_{identifier}config_old");
    }

    void IPostInjectInit.PostInject()
    {
        _sawmill = _logManager.GetSawmill("parallax.generated");
    }

    private sealed class CacheDatum
    {
        public required ResPath ConfigPath;
        public required Task<Texture> LoadTask;
        public required CancellationTokenSource CancellationSource;
        public ValueList<CancellationTokenRegistration> CancelRegistrations;

        public int RefCount;
    }
}
