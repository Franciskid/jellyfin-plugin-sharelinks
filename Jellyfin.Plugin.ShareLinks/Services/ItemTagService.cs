using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ShareLinks.Services;

/// <summary>Applies and removes temporary tags on shared items.</summary>
public sealed class ItemTagService
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<ItemTagService> _logger;

    /// <summary>Initializes a new instance of the <see cref="ItemTagService"/> class.</summary>
    public ItemTagService(ILibraryManager libraryManager, ILogger<ItemTagService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>Ensures the supplied tag is present on the item and persisted.</summary>
    public async Task<bool> EnsureTagAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag cannot be empty.", nameof(tag));
        }

        var tags = item.Tags?.ToList() ?? new List<string>();
        if (tags.Any(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        tags.Add(tag);
        item.Tags = tags.ToArray();
        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ShareLinks: applied temporary tag {Tag} to item {ItemId}.", tag, item.Id);
        return true;
    }

    /// <summary>Removes the supplied tag from the item and persists the change.</summary>
    public async Task<bool> RemoveTagAsync(BaseItem item, string tag, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            throw new ArgumentNullException(nameof(item));
        }

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("Tag cannot be empty.", nameof(tag));
        }

        var tags = item.Tags?.ToList() ?? new List<string>();
        var removed = tags.RemoveAll(existing => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)) > 0;
        if (!removed)
        {
            return false;
        }

        item.Tags = tags.ToArray();
        await PersistAsync(item, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ShareLinks: removed temporary tag {Tag} from item {ItemId}.", tag, item.Id);
        return true;
    }

    private async Task PersistAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var method = _libraryManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(candidate =>
            {
                if (!string.Equals(candidate.Name, "UpdateItemAsync", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 4
                    && typeof(BaseItem).IsAssignableFrom(parameters[0].ParameterType)
                    && typeof(BaseItem).IsAssignableFrom(parameters[1].ParameterType)
                    && parameters[3].ParameterType == typeof(CancellationToken);
            });

        if (method is null)
        {
            throw new MissingMethodException(_libraryManager.GetType().FullName, "UpdateItemAsync");
        }

        var parametersInfo = method.GetParameters();
        var updateReason = parametersInfo[2].ParameterType.IsEnum
            ? Enum.ToObject(parametersInfo[2].ParameterType, 0)
            : 0;

        var parent = item.DisplayParent ?? item;
        var task = method.Invoke(_libraryManager, new object?[]
        {
            item,
            parent,
            updateReason,
            cancellationToken
        }) as Task;

        if (task is null)
        {
            throw new InvalidOperationException("UpdateItemAsync did not return a task.");
        }

        await task.ConfigureAwait(false);
    }
}
