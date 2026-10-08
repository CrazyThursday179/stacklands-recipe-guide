using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RecipeGuide;

public sealed class RecipeLinkText : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler,
    IPointerMoveHandler, IPointerClickHandler
{
    private TMP_Text? text;
    private string source = string.Empty;
    private IReadOnlyCollection<string> visited = Array.Empty<string>();
    private string? hoveredId;
    private int handledClickFrame = -1;
    private string? handledClickId;

    public int SourcePanelIndex { get; set; } = -1;

    public void Initialise(TMP_Text target)
    {
        Bind(target);
        Render();
    }

    public void Bind(TMP_Text target)
    {
        text = target;
        text.raycastTarget = true;
    }

    public void SetSource(string value, IReadOnlyCollection<string> visitedIds)
    {
        source = RecipeGuideController.RemoveGeneratedLinkTags(value ?? string.Empty);
        visited = visitedIds;
        Render();
    }

    public void OnPointerEnter(PointerEventData eventData) => UpdateHover(eventData);

    public void OnPointerMove(PointerEventData eventData) => UpdateHover(eventData);

    public void OnPointerExit(PointerEventData eventData)
    {
        if (hoveredId == null)
            return;

        hoveredId = null;
        Render();
    }

    public void OnPointerClick(PointerEventData eventData)
    {
        if (eventData.button != PointerEventData.InputButton.Left || text == null)
            return;

        var linkId = FindLinkId(eventData.position, eventData.pressEventCamera);
        if (linkId != null)
            OpenLink(linkId);
    }

    public void ProcessPointer(Vector2 position, bool clicked)
    {
        if (text == null)
            return;

        text.ForceMeshUpdate();
        var linkId = FindLinkId(position, null);
        SetHoveredLink(linkId);
        if (clicked && linkId != null)
            OpenLink(linkId);
    }

    private void UpdateHover(PointerEventData eventData)
    {
        if (text == null)
            return;

        var linkId = FindLinkId(eventData.position, eventData.pressEventCamera);
        SetHoveredLink(linkId);
    }

    private string? FindLinkId(Vector2 position, Camera? camera)
    {
        if (text == null)
            return null;

        var linkIndex = TMP_TextUtilities.FindIntersectingLink(text, position, camera);
        if (linkIndex < 0 || linkIndex >= text.textInfo.linkCount)
            return null;

        var linkId = text.textInfo.linkInfo[linkIndex].GetLinkID();
        return linkId.StartsWith(RecipeGuideController.LinkPrefix, StringComparison.Ordinal)
            ? linkId
            : null;
    }

    private void SetHoveredLink(string? linkId)
    {
        var newId = linkId == null
            ? null
            : linkId[RecipeGuideController.LinkPrefix.Length..];
        if (newId == hoveredId)
            return;

        hoveredId = newId;
        Render();
    }

    private void OpenLink(string linkId)
    {
        var cardId = linkId[RecipeGuideController.LinkPrefix.Length..];
        if (handledClickFrame == Time.frameCount && handledClickId == cardId)
            return;

        handledClickFrame = Time.frameCount;
        handledClickId = cardId;
        RecipeGuideController.Instance?.OpenRecipeFromLink(cardId, SourcePanelIndex);
    }

    private void Render()
    {
        if (text == null)
            return;

        text.text = RecipeGuideController.Instance == null
            ? source
            : RecipeGuideController.Instance.WrapRecipeNames(source, visited, hoveredId);
        text.ForceMeshUpdate();
    }
}
