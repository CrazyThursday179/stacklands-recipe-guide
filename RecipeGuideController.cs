using System.Text;
using System.Text.RegularExpressions;
using DG.Tweening;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace RecipeGuide;

public sealed class RecipeGuideController : MonoBehaviour
{
    public const string LinkPrefix = "cc:";
    private const int MaxDepth = 10;
    private const float PanelGap = 12f;
    private const float PanelAnimationDuration = 0.2f;

    public static RecipeGuideController? Instance { get; private set; }

    private readonly List<RecipePanel> panels = new();
    private readonly Dictionary<string, CardView> craftableCards = new(StringComparer.Ordinal);
    private Snapshot? lockedSnapshot;
    private GameCard? pendingCard;
    private int handledClickFrame = -1;
    private int suppressGlobalClickFrame = -1;
    private int lastScreenWidth = -1;
    private int lastScreenHeight = -1;
    private WorldManager? cachedWorld;
    private int cachedBlueprintCount = -1;
    private GameScreen? cachedIdeaScreen;
    private string cachedIdeaFingerprint = string.Empty;
    private readonly Dictionary<string, IdeaEntry> ideaEntries = new(StringComparer.Ordinal);
    private int ideaRegistryVersion;
    private int cachedIdeaRegistryVersion = -1;
    private Image? lockPin;
    private Sprite? lockPinSprite;
    private Texture2D? lockPinTexture;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void Update()
    {
        var screen = GameScreen.instance;
        if (screen == null || screen.InfoBox == null)
            return;

        // Recipe panels live on the overlay Canvas rather than under the
        // original InfoBox, so Options/ESC cannot hide them automatically.
        // Close the generated chain as soon as the player leaves the main
        // game view.  The visibility check also covers menu transitions that
        // do not come from the keyboard.
        if (Keyboard.current?.escapeKey.wasPressedThisFrame == true
            || !screen.InfoBox.activeInHierarchy)
        {
            CloseAllPanels();
        }
    }

    private void OnDestroy()
    {
        if (lockPin != null)
            Destroy(lockPin.gameObject);
        if (lockPinSprite != null)
            Destroy(lockPinSprite);
        if (lockPinTexture != null)
            Destroy(lockPinTexture);

        if (Instance == this)
            Instance = null;
    }

    public void RequestCardLock(GameCard card)
    {
        pendingCard = card;
        suppressGlobalClickFrame = Time.frameCount;
    }

    public void LockKnowledge(IKnowledge knowledge)
    {
        if (knowledge == null)
            return;

        var id = knowledge.CardId ?? string.Empty;
        Lock(new Snapshot(knowledge.KnowledgeName ?? string.Empty, knowledge.KnowledgeText ?? string.Empty, id));
    }

    public void LockQuest(AchievementElement element)
    {
        if (element == null || element.MyQuest == null)
            return;

        var title = SokLoc.Translate("label_quest");
        Lock(new Snapshot(title, element.MyQuest.Description ?? string.Empty, string.Empty));
    }

    public void LockCardopediaEntry(CardopediaEntryElement element)
    {
        if (element?.MyCardData == null)
            return;

        LockCardData(element.MyCardData);
    }

    public void OpenRecipeFromLink(string cardId, int sourcePanelIndex)
    {
        var sourcePath = sourcePanelIndex < 0
            ? GetMainPath()
            : sourcePanelIndex < panels.Count ? panels[sourcePanelIndex].Path : GetMainPath();

        if (!HasRecipe(cardId)
            || !TryGetIdeaKnowledge(cardId, out var knowledge)
            || sourcePath.Contains(cardId, StringComparer.Ordinal)
            || sourcePath.Count >= MaxDepth)
            return;

        var requestedPath = sourcePath.Concat(new[] { cardId }).ToList();
        if (panels.Any(panel => panel.Path.SequenceEqual(requestedPath)))
            return;

        // The game's original lower info box is always the root of the
        // expandable chain.  Clicking another recipe link in that box must
        // replace level 2, rather than appending a new level after whatever
        // chain happened to be open previously.
        if (sourcePanelIndex < 0 && panels.Count > 0)
            CloseAllPanels();

        suppressGlobalClickFrame = Time.frameCount;
        if (sourcePanelIndex >= 0 && sourcePanelIndex < panels.Count && sourcePanelIndex + 1 < panels.Count)
        {
            for (var i = panels.Count - 1; i > sourcePanelIndex; i--)
                ClosePanel(i);
        }

        if (lockedSnapshot == null)
        {
            var screen = GameScreen.instance;
            if (screen != null)
                Lock(new Snapshot(screen.InfoTitle.text, screen.InfoText.text, string.Empty));
        }

        if (panels.Count >= MaxDepth)
            return;

        CreatePanel(cardId, requestedPath, !IsIdeaUnlocked(knowledge));
    }

    public void OnGameScreenUpdated(GameScreen screen)
    {
        if (screen == null || screen.InfoTitle == null || screen.InfoText == null)
            return;

        if (!screen.InfoBox.activeInHierarchy)
        {
            CloseAllPanels();
            return;
        }

        if (pendingCard != null)
        {
            var card = pendingCard;
            pendingCard = null;
            var title = RemoveGeneratedLinkTags(screen.InfoTitle.text);
            var text = RemoveGeneratedLinkTags(screen.InfoText.text);
            if (string.IsNullOrWhiteSpace(title) && card.CardData != null)
            {
                title = card.CardData.FullName ?? card.CardData.Name ?? card.CardData.Id;
                text = card.CardData.Description ?? string.Empty;
            }

            Lock(new Snapshot(title, text, card.CardData?.Id ?? string.Empty));
        }

        if (Mouse.current?.leftButton.wasPressedThisFrame == true && handledClickFrame != Time.frameCount)
        {
            handledClickFrame = Time.frameCount;
            if (suppressGlobalClickFrame != Time.frameCount)
                HandleUnclaimedClick(screen);
        }

        var vanillaTitle = screen.InfoTitle.text;
        var vanillaText = screen.InfoText.text;
        var sourceTitle = lockedSnapshot?.Title ?? vanillaTitle;
        var sourceText = lockedSnapshot?.Text ?? vanillaText;
        IReadOnlyCollection<string> sourcePath = lockedSnapshot == null
            ? Array.Empty<string>()
            : GetMainPath();

        screen.InfoTitle.text = RemoveGeneratedLinkTags(sourceTitle);
        screen.InfoText.text = RemoveGeneratedLinkTags(sourceText);
        ApplyLinks(screen.InfoText, sourcePath, -1);
        ProcessRecipePointers(screen, Mouse.current?.position.ReadValue() ?? Vector2.zero,
            Mouse.current?.leftButton.wasPressedThisFrame == true);
        UpdateLockPin(screen);

        // ShowInfoBox owns these static values while the player is only
        // hovering a vanilla UI element such as a booster-pack purchase
        // button.  Writing the current screen text back every frame can erase
        // that delayed hover description.  Persist the cache only when this
        // mod is displaying a locked snapshot or has actually transformed
        // the vanilla text with recipe links.
        var modChangedTitle = !string.Equals(screen.InfoTitle.text, vanillaTitle, StringComparison.Ordinal);
        var modChangedText = !string.Equals(screen.InfoText.text, vanillaText, StringComparison.Ordinal);
        if (lockedSnapshot != null || modChangedTitle || modChangedText)
        {
            GameScreen.InfoBoxTitle = screen.InfoTitle.text;
            GameScreen.InfoBoxText = screen.InfoText.text;
        }
        if (lastScreenWidth != Screen.width || lastScreenHeight != Screen.height)
        {
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            UpdatePanelPositions(screen);
        }
    }

    public string WrapRecipeNames(string source, IReadOnlyCollection<string> visitedIds, string? hoveredId = null)
    {
        var result = RemoveGeneratedLinkTags(source ?? string.Empty);
        var entries = GetCraftableCards()
            .Where(x => !visitedIds.Contains(x.Id, StringComparer.Ordinal))
            .OrderByDescending(x => x.Names.Max(name => name.Length))
            .ToList();

        var placeholders = new List<(string Token, CardView Card, string Name)>();
        foreach (var card in entries)
        {
            var matchedName = card.Names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderByDescending(name => name.Length)
                .FirstOrDefault(name => result.Contains(name, StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(matchedName))
                continue;

            var escaped = Regex.Escape(matchedName);
            var token = $"\uE000{placeholders.Count}\uE001";
            result = Regex.Replace(result, escaped, match =>
            {
                placeholders.Add((token, card, matchedName));
                return token;
            }, RegexOptions.CultureInvariant);
        }

        foreach (var placeholder in placeholders)
        {
            var inner = string.Equals(placeholder.Card.Id, hoveredId, StringComparison.Ordinal)
                ? $"<u>{placeholder.Name}</u>"
                : placeholder.Name;
            result = result.Replace(placeholder.Token,
                $"<link=\"{LinkPrefix}{placeholder.Card.Id}\">{inner}</link>", StringComparison.Ordinal);
        }

        return result;
    }

    public static string RemoveGeneratedLinkTags(string source)
    {
        if (string.IsNullOrEmpty(source))
            return string.Empty;

        source = Regex.Replace(source, "<link=\\\"cc:[^\\\"]+\\\">", string.Empty,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        source = source.Replace("</link>", string.Empty, StringComparison.OrdinalIgnoreCase);
        source = source.Replace("<u>", string.Empty, StringComparison.OrdinalIgnoreCase);
        source = source.Replace("</u>", string.Empty, StringComparison.OrdinalIgnoreCase);
        return source;
    }

    private void Lock(Snapshot snapshot)
    {
        lockedSnapshot = snapshot;
        suppressGlobalClickFrame = Time.frameCount;
        CloseAllPanels();
    }

    private void LockCardData(CardData card)
    {
        var title = card.FullName ?? card.Name ?? card.Id;
        var text = card.Description ?? string.Empty;
        Lock(new Snapshot(title, text, card.Id ?? string.Empty));
    }

    private void HandleUnclaimedClick(GameScreen screen)
    {
        var mousePosition = Mouse.current?.position.ReadValue() ?? Vector2.zero;
        if (IsOverAnyRecipePanel(mousePosition))
            return;

        // The lower information panel is not map blank space. Clicking it should
        // keep the locked snapshot, while RecipeLinkText handles link clicks.
        if (IsOverInfoBox(screen, mousePosition))
            return;

        var hoveredCard = WorldManager.instance?.HoveredCard;
        if (hoveredCard != null && hoveredCard.CardData != null)
        {
            Lock(new Snapshot(
                RemoveGeneratedLinkTags(screen.InfoTitle.text),
                RemoveGeneratedLinkTags(screen.InfoText.text),
                hoveredCard.CardData.Id ?? string.Empty));
            return;
        }

        if (!IsOverSidePanel(screen, mousePosition))
            ClearLocked();
    }

    private void ClearLocked()
    {
        lockedSnapshot = null;
        CloseAllPanels();
    }

    private void ApplyLinks(TMP_Text text, IReadOnlyCollection<string> visited, int panelIndex)
    {
        if (text == null)
            return;

        var source = text.text;
        var component = text.GetComponent<RecipeLinkText>();
        if (component == null)
            component = text.gameObject.AddComponent<RecipeLinkText>();
        component.Bind(text);
        component.SourcePanelIndex = panelIndex;
        component.SetSource(source, visited);
    }

    private void UpdateLockPin(GameScreen screen)
    {
        if (screen.InfoBox == null)
            return;

        if (lockPin == null)
        {
            var pinObject = new GameObject("RecipeGuideLockPin");
            pinObject.transform.SetParent(screen.InfoBox.transform, false);
            lockPin = pinObject.AddComponent<Image>();
            lockPin.sprite = lockPinSprite ??= CreateLockPinSprite();
            lockPin.raycastTarget = false;

            var layoutElement = pinObject.AddComponent<LayoutElement>();
            layoutElement.ignoreLayout = true;

            var rect = pinObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(1f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(1f, 1f);
            rect.sizeDelta = new Vector2(34f, 34f);
            rect.anchoredPosition = new Vector2(-10f, -10f);
        }

        lockPin.enabled = lockedSnapshot != null;
        if (lockPin.enabled)
            lockPin.transform.SetAsLastSibling();
    }

    private Sprite CreateLockPinSprite()
    {
        const int size = 48;
        lockPinTexture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };

        var pixels = Enumerable.Repeat(new Color(0f, 0f, 0f, 0f), size * size).ToArray();
        var ink = new Color(0.12f, 0.09f, 0.06f, 0.95f);
        DrawCircle(pixels, size, new Vector2(24f, 14f), 9f, ink);
        DrawLine(pixels, size, new Vector2(24f, 20f), new Vector2(24f, 35f), 7f, ink);
        DrawLine(pixels, size, new Vector2(24f, 29f), new Vector2(24f, 44f), 3f, ink);
        lockPinTexture.SetPixels(pixels);
        lockPinTexture.Apply();
        return Sprite.Create(lockPinTexture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), size);
    }

    private static void DrawCircle(Color[] pixels, int size, Vector2 centre, float radius, Color colour)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(centre.x - radius - 1f));
        var maxX = Mathf.Min(size - 1, Mathf.CeilToInt(centre.x + radius + 1f));
        var minY = Mathf.Max(0, Mathf.FloorToInt(centre.y - radius - 1f));
        var maxY = Mathf.Min(size - 1, Mathf.CeilToInt(centre.y + radius + 1f));
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var distance = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), centre);
                if (distance <= radius)
                    pixels[y * size + x] = colour;
            }
        }
    }

    private static void DrawLine(Color[] pixels, int size, Vector2 start, Vector2 end, float width, Color colour)
    {
        var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.x, end.x) - width));
        var maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(start.x, end.x) + width));
        var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(start.y, end.y) - width));
        var maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(start.y, end.y) + width));
        var direction = end - start;
        var lengthSquared = direction.sqrMagnitude;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var point = new Vector2(x + 0.5f, y + 0.5f);
                var projection = lengthSquared <= Mathf.Epsilon
                    ? 0f
                    : Mathf.Clamp01(Vector2.Dot(point - start, direction) / lengthSquared);
                var closest = start + direction * projection;
                if (Vector2.Distance(point, closest) <= width * 0.5f)
                    pixels[y * size + x] = colour;
            }
        }
    }

    private void CreatePanel(string cardId, IReadOnlyCollection<string> path, bool locked = false)
    {
        var screen = GameScreen.instance;
        if (screen == null || screen.InfoBox == null)
            return;

        var canvas = GetOverlayCanvas(screen);
        if (canvas == null)
            return;

        var clone = Instantiate(screen.InfoBox, canvas.transform, false);
        clone.name = $"RecipeGuidePanel_{panels.Count + 1}";
        clone.SetActive(false);

        var cloneLayout = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
        cloneLayout.ignoreLayout = true;
        foreach (var pin in clone.GetComponentsInChildren<Transform>(true)
                     .Where(transform => transform.name == "RecipeGuideLockPin"))
            pin.gameObject.SetActive(false);

        foreach (var tooltip in clone.GetComponentsInChildren<ShowInfoBox>(true))
            tooltip.enabled = false;

        var title = FindText(clone, screen.InfoTitle);
        var text = FindText(clone, screen.InfoText);
        if (title == null || text == null)
        {
            Destroy(clone);
            return;
        }

        var panel = new RecipePanel(clone, cardId, path.ToList(), title, text);
        panels.Add(panel);
        var enteringIndex = panels.Count - 1;
        if (locked && TryGetIdeaKnowledge(cardId, out var lockedKnowledge))
        {
            title.text = RemoveGeneratedLinkTags(lockedKnowledge.KnowledgeName ?? GetCardDisplayName(cardId));
            text.text = SokLoc.Translate("recipe_guide_locked_text");
        }
        else if (TryGetIdeaKnowledge(cardId, out var knowledge))
        {
            title.text = RemoveGeneratedLinkTags(knowledge.KnowledgeName ?? GetCardDisplayName(cardId));
            text.text = RemoveGeneratedLinkTags(knowledge.KnowledgeText ?? string.Empty);
        }
        else
        {
            title.text = GetCardDisplayName(cardId);
            text.text = BuildRecipeBody(cardId);
        }
        ApplyLinks(text, panel.Path, panels.Count - 1);

        var rect = clone.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        var canvasGroup = clone.GetComponent<CanvasGroup>() ?? clone.AddComponent<CanvasGroup>();
        var target = GetPanelTargetPosition(screen, enteringIndex, rect);
        rect.anchoredPosition = target + Vector2.left * 24f;
        canvasGroup.alpha = 0f;
        clone.transform.SetAsLastSibling();
        ReflowPanelPositions(screen, enteringIndex);
        clone.SetActive(true);
        rect.DOAnchorPos(target, PanelAnimationDuration).SetEase(Ease.OutCubic);
        canvasGroup.DOFade(1f, PanelAnimationDuration).SetEase(Ease.OutQuad);
    }

    private string BuildRecipeBody(string cardId)
    {
        var card = FindCard(cardId);
        var builder = new StringBuilder();
        if (card != null && !string.IsNullOrWhiteSpace(card.Description))
            builder.AppendLine(card.Description);

        var recipes = FindRecipes(cardId).ToList();
        if (recipes.Count == 0)
            return builder.ToString().TrimEnd();

        if (builder.Length > 0)
            builder.AppendLine().AppendLine();

        builder.AppendLine("Recipes / 配方:");
        for (var i = 0; i < recipes.Count; i++)
        {
            var ingredients = FormatIngredients(recipes[i].Subprint.RequiredCards);
            var result = GetCardDisplayName(recipes[i].Subprint.ResultCard ?? cardId);
            builder.Append(ingredients).Append("  →  ").AppendLine(result);
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatIngredients(string[]? ingredients)
    {
        if (ingredients == null || ingredients.Length == 0)
            return "—";

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var id in ingredients)
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            if (!counts.ContainsKey(id))
                order.Add(id);
            counts[id] = counts.TryGetValue(id, out var count) ? count + 1 : 1;
        }

        return string.Join(" + ", order.Select(id =>
        {
            var count = counts[id];
            var name = GetCardDisplayName(id);
            return count == 1 ? name : $"{count}x {name}";
        }));
    }

    private void CloseAllPanels()
    {
        for (var i = panels.Count - 1; i >= 0; i--)
            ClosePanel(i);
    }

    private void ClosePanel(int index)
    {
        if (index < 0 || index >= panels.Count)
            return;

        var panel = panels[index];
        panels.RemoveAt(index);
        var rect = panel.Root.GetComponent<RectTransform>();
        var group = panel.Root.GetComponent<CanvasGroup>() ?? panel.Root.AddComponent<CanvasGroup>();
        rect.DOKill();
        group.DOKill();
        rect.DOAnchorPos(rect.anchoredPosition + Vector2.left * 18f, PanelAnimationDuration)
            .SetEase(Ease.InCubic);
        group.DOFade(0f, PanelAnimationDuration).SetEase(Ease.InQuad)
            .OnComplete(() => Destroy(panel.Root));
    }

    private void UpdatePanelPositions(GameScreen screen)
    {
        ReflowPanelPositions(screen, -1);
    }

    private void ReflowPanelPositions(GameScreen screen, int enteringIndex)
    {
        for (var i = 0; i < panels.Count; i++)
        {
            var rect = panels[i].Root.GetComponent<RectTransform>();
            if (rect == null)
                continue;

            var target = GetPanelTargetPosition(screen, i, rect);
            if (i != enteringIndex)
            {
                rect.DOKill(false);
                rect.DOAnchorPos(target, PanelAnimationDuration).SetEase(Ease.OutCubic);
            }
            panels[i].Root.transform.SetSiblingIndex(panels[i].Root.transform.parent.childCount - 1);
        }
    }

    private Vector2 GetPanelTargetPosition(GameScreen screen, int index, RectTransform panelRect)
    {
        var canvas = GetOverlayCanvas(screen);
        var root = canvas?.GetComponent<RectTransform>();
        var original = screen.InfoBox.GetComponent<RectTransform>();
        if (root == null || original == null)
            return panelRect.anchoredPosition;

        var camera = canvas!.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
        var originalCorners = new Vector3[4];
        original.GetWorldCorners(originalCorners);
        var originalRight = RectTransformUtility.WorldToScreenPoint(camera, originalCorners[2]).x;
        var originalPivot = RectTransformUtility.WorldToScreenPoint(camera, original.position);

        var panelCorners = new Vector3[4];
        panelRect.GetWorldCorners(panelCorners);
        var panelLeft = RectTransformUtility.WorldToScreenPoint(camera, panelCorners[0]);
        var panelRight = RectTransformUtility.WorldToScreenPoint(camera, panelCorners[3]);
        var width = Mathf.Abs(panelRight.x - panelLeft.x);
        if (width <= 0f)
            width = panelRect.rect.width;

        var normalStart = originalRight + PanelGap;
        var latestStart = Screen.width - PanelGap - width;
        var panelCount = Mathf.Max(1, panels.Count);
        if (panelCount == 1)
            return ScreenToCanvasPosition(canvas, camera,
                new Vector2(normalStart + width * panelRect.pivot.x, originalPivot.y));

        // Keep ordinary panels side by side while the complete chain fits.
        // Only compress the horizontal step after the last panel would cross
        // the actual screen edge.  This prevents the old premature stacking
        // around the middle of the play area.
        var normalStep = width + PanelGap;
        var fitsNormally = normalStart + normalStep * (panelCount - 1) <= latestStart;
        var step = fitsNormally
            ? normalStep
            : (latestStart - normalStart) / (panelCount - 1);
        if (step <= 0f)
            step = Mathf.Max(24f, width * 0.08f);

        var firstStart = normalStart;
        var left = firstStart + step * index;
        return ScreenToCanvasPosition(canvas, camera,
            new Vector2(left + width * panelRect.pivot.x, originalPivot.y));
    }

    private static Vector2 ScreenToCanvasPosition(Canvas canvas, Camera? camera, Vector2 screenPosition)
    {
        var canvasRect = canvas.GetComponent<RectTransform>();
        return canvasRect != null
            && RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, camera, out var localPoint)
            ? localPoint
            : Vector2.zero;
    }

    private static Canvas? GetOverlayCanvas(GameScreen screen)
    {
        return screen.InfoBox.GetComponentInParent<Canvas>();
    }

    private bool IsOverAnyRecipePanel(Vector2 mousePosition)
    {
        foreach (var panel in panels)
        {
            var rect = panel.Root.GetComponent<RectTransform>();
            if (rect != null && RectTransformUtility.RectangleContainsScreenPoint(rect, mousePosition, null))
                return true;
        }

        return false;
    }

    private static bool IsOverSidePanel(GameScreen screen, Vector2 mousePosition)
    {
        var side = screen.SideTransform;
        return side != null && RectTransformUtility.RectangleContainsScreenPoint(side, mousePosition, null);
    }

    private static bool IsOverInfoBox(GameScreen screen, Vector2 mousePosition)
    {
        var infoBox = screen.InfoBox?.GetComponent<RectTransform>();
        return infoBox != null && RectTransformUtility.RectangleContainsScreenPoint(infoBox, mousePosition, null);
    }

    private List<string> GetMainPath()
    {
        if (lockedSnapshot == null || string.IsNullOrWhiteSpace(lockedSnapshot.RootId))
            return new List<string>();
        return new List<string> { ResolveRecipeCardId(lockedSnapshot.RootId) };
    }

    private TMP_Text? FindText(GameObject root, TMP_Text source)
    {
        return root.GetComponentsInChildren<TMP_Text>(true)
            .FirstOrDefault(x => x.gameObject.name == source.gameObject.name)
            ?? root.GetComponentsInChildren<TMP_Text>(true).OrderBy(x => x.transform.GetSiblingIndex()).FirstOrDefault();
    }

    private string GetCardDisplayName(string id)
    {
        return FindCard(id)?.FullName ?? id;
    }

    private CardData? FindCard(string id)
    {
        var world = WorldManager.instance;
        if (world == null || string.IsNullOrWhiteSpace(id))
            return null;

        return world.CardDataPrefabs.FirstOrDefault(x => x.Id == id)
            ?? world.BlueprintPrefabs.FirstOrDefault(x => x.Id == id);
    }

    private void RegisterIdeaKnowledge(IKnowledge knowledge)
    {
        if (knowledge == null || string.IsNullOrWhiteSpace(knowledge.CardId))
            return;

        var names = new List<string>();
        AddName(names, knowledge.KnowledgeName);
        if (ideaEntries.TryGetValue(knowledge.CardId, out var existing)
            && existing.Names.SequenceEqual(names, StringComparer.Ordinal))
        {
            existing.Knowledge = knowledge;
            return;
        }

        ideaEntries[knowledge.CardId] = new IdeaEntry(knowledge.CardId, names, knowledge);
        ideaRegistryVersion++;
    }

    private static bool IsIdeaUnlocked(IKnowledge knowledge)
    {
        var save = WorldManager.instance?.CurrentSave;
        return save?.FoundCardIds != null && save.FoundCardIds.Contains(knowledge.CardId);
    }

    private void ResetIdeaKnowledgeRegistry()
    {
        ideaEntries.Clear();
        ideaRegistryVersion++;
        cachedIdeaFingerprint = string.Empty;
        cachedIdeaRegistryVersion = -1;
    }

    private string ResolveRecipeCardId(string ideaId)
    {
        var recipe = FindRecipes(ideaId).FirstOrDefault();
        return recipe?.Subprint.ResultCard ?? ideaId;
    }

    private bool TryGetIdeaKnowledge(string cardId, out IKnowledge knowledge)
    {
        foreach (var entry in ideaEntries.Values)
        {
            if (string.Equals(entry.Id, cardId, StringComparison.Ordinal)
                || string.Equals(ResolveRecipeCardId(entry.Id), cardId, StringComparison.Ordinal))
            {
                knowledge = entry.Knowledge;
                return true;
            }
        }

        knowledge = null!;
        return false;
    }

    private IEnumerable<CardView> GetCraftableCards()
    {
        var world = WorldManager.instance;
        var screen = GameScreen.instance;
        if (world == null || screen == null)
            return Array.Empty<CardView>();

        var ideaElements = screen.GetComponentsInChildren<IdeaElement>(true)
            .Where(element => element != null && element.MyKnowledge != null)
            .ToList();
        foreach (var element in ideaElements)
        {
            if (element.MyKnowledge != null)
                RegisterIdeaKnowledge(element.MyKnowledge);
        }

        var fingerprint = string.Join("\n", ideaEntries.Values
            .Select(entry => $"{entry.Id}\u001f{string.Join("\u001e", entry.Names)}\u001f{IsIdeaUnlocked(entry.Knowledge)}")
            .OrderBy(value => value, StringComparer.Ordinal));

        if (!ReferenceEquals(cachedWorld, world)
            || !ReferenceEquals(cachedIdeaScreen, screen)
            || cachedBlueprintCount != world.BlueprintPrefabs.Count
            || cachedIdeaRegistryVersion != ideaRegistryVersion
            || !string.Equals(cachedIdeaFingerprint, fingerprint, StringComparison.Ordinal))
        {
            cachedWorld = world;
            cachedIdeaScreen = screen;
            cachedBlueprintCount = world.BlueprintPrefabs.Count;
            cachedIdeaRegistryVersion = ideaRegistryVersion;
            cachedIdeaFingerprint = fingerprint;
            craftableCards.Clear();
            foreach (var entry in ideaEntries.Values)
            {
                var id = ResolveRecipeCardId(entry.Id);
                if (!HasRecipe(entry.Id))
                    continue;

                var names = new List<string>(entry.Names);
                var card = FindCard(id);
                if (card != null)
                {
                    AddName(names, card.FullName);
                    AddName(names, card.Name);
                }

                if (names.Count > 0)
                    craftableCards[id] = new CardView(id, names);
            }
        }

        return craftableCards.Values;
    }

    private void ProcessRecipePointers(GameScreen screen, Vector2 position, bool clicked)
    {
        screen.InfoText.GetComponent<RecipeLinkText>()?.ProcessPointer(position, clicked);
        foreach (var panel in panels.ToList())
            panel.Text.GetComponent<RecipeLinkText>()?.ProcessPointer(position, clicked);
    }

    private static void AddName(List<string> names, string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.Ordinal))
            names.Add(name);
    }

    private bool HasRecipe(string id) => FindRecipes(id).Any();

    private IEnumerable<Recipe> FindRecipes(string cardId)
    {
        var world = WorldManager.instance;
        if (world == null)
            yield break;

        foreach (var blueprint in world.BlueprintPrefabs)
        {
            if (blueprint.Subprints == null)
                continue;

            var blueprintMatches = string.Equals(blueprint.CardId, cardId, StringComparison.Ordinal);
            foreach (var subprint in blueprint.Subprints)
            {
                if (subprint != null
                    && (blueprintMatches || string.Equals(subprint.ResultCard, cardId, StringComparison.Ordinal)))
                    yield return new Recipe(blueprint, subprint);
            }
        }
    }

    private sealed class Snapshot
    {
        public Snapshot(string title, string text, string rootId)
        {
            Title = title;
            Text = text;
            RootId = rootId;
        }

        public string Title { get; }
        public string Text { get; }
        public string RootId { get; }
    }

    private sealed class Recipe
    {
        public Recipe(Blueprint blueprint, Subprint subprint)
        {
            Blueprint = blueprint;
            Subprint = subprint;
        }

        public Blueprint Blueprint { get; }
        public Subprint Subprint { get; }
    }

    private sealed class CardView
    {
        public CardView(string id, IReadOnlyList<string> names)
        {
            Id = id;
            Names = names;
        }

        public string Id { get; }
        public IReadOnlyList<string> Names { get; }
    }

    private sealed class IdeaEntry
    {
        public IdeaEntry(string id, IReadOnlyList<string> names, IKnowledge knowledge)
        {
            Id = id;
            Names = names;
            Knowledge = knowledge;
        }

        public string Id { get; }
        public IReadOnlyList<string> Names { get; }
        public IKnowledge Knowledge { get; set; }
    }

    private sealed class RecipePanel
    {
        public RecipePanel(GameObject root, string cardId, List<string> path, TMP_Text title, TMP_Text text)
        {
            Root = root;
            CardId = cardId;
            Path = path;
            Title = title;
            Text = text;
        }

        public GameObject Root { get; }
        public string CardId { get; }
        public List<string> Path { get; }
        public TMP_Text Title { get; }
        public TMP_Text Text { get; }
    }

    [HarmonyPatch(typeof(GameScreen), nameof(GameScreen.InitIdeaElements))]
    private static class GameScreenInitIdeasPatch
    {
        private static void Prefix() => Instance?.ResetIdeaKnowledgeRegistry();
    }

    [HarmonyPatch(typeof(IdeaElement), nameof(IdeaElement.SetKnowledge))]
    private static class IdeaElementSetKnowledgePatch
    {
        private static void Postfix(IKnowledge __0) => Instance?.RegisterIdeaKnowledge(__0);
    }

    [HarmonyPatch(typeof(GameScreen), "Update")]
    private static class GameScreenUpdatePatch
    {
        private static void Postfix(GameScreen __instance) => Instance?.OnGameScreenUpdated(__instance);
    }

    [HarmonyPatch(typeof(GameCard), nameof(GameCard.Clicked))]
    private static class GameCardClickedPatch
    {
        private static void Postfix(GameCard __instance) => Instance?.RequestCardLock(__instance);
    }

    [HarmonyPatch(typeof(CustomButton), nameof(CustomButton.OnPointerUp))]
    private static class CustomButtonPointerPatch
    {
        private static void Postfix(CustomButton __instance, PointerEventData eventData)
        {
            if (eventData.button != PointerEventData.InputButton.Left || Instance == null)
                return;

            var idea = __instance.GetComponentInParent<IdeaElement>();
            if (idea?.MyKnowledge != null)
            {
                Instance.LockKnowledge(idea.MyKnowledge);
                return;
            }

            var quest = __instance.GetComponentInParent<AchievementElement>();
            if (quest?.MyQuest != null)
            {
                Instance.LockQuest(quest);
                return;
            }

            var entry = __instance.GetComponentInParent<CardopediaEntryElement>();
            if (entry?.MyCardData != null)
                Instance.LockCardopediaEntry(entry);
        }
    }
}
