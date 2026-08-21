namespace BrowserServer
{
    /// <summary>Which context menu entries are valid for a server offer (mirrors UWP client rules).</summary>
    public static class ContextMenuRules
    {
        public static bool CanOpenNewTab(ContextMenuOfferPayload offer)
        {
            return HasLinkOrImage(offer);
        }

        public static bool CanCopyLink(ContextMenuOfferPayload offer)
        {
            return HasLinkOrImage(offer);
        }

        public static bool CanCopyText(ContextMenuOfferPayload offer)
        {
            return offer != null && !string.IsNullOrWhiteSpace(offer.text);
        }

        public static bool CanSavePicture(ContextMenuOfferPayload offer)
        {
            return offer != null && !string.IsNullOrWhiteSpace(offer.imageUrl);
        }

        public static bool CanShare(ContextMenuOfferPayload offer)
        {
            return HasLinkOrImage(offer);
        }

        public static string GetShareUrl(ContextMenuOfferPayload offer)
        {
            if (offer == null)
                return null;
            if (!string.IsNullOrWhiteSpace(offer.linkUrl))
                return offer.linkUrl.Trim();
            if (!string.IsNullOrWhiteSpace(offer.imageUrl))
                return offer.imageUrl.Trim();
            return null;
        }

        public static string GetCopyLinkUrl(ContextMenuOfferPayload offer)
        {
            return GetShareUrl(offer);
        }

        static bool HasLinkOrImage(ContextMenuOfferPayload offer)
        {
            return offer != null
                && (!string.IsNullOrWhiteSpace(offer.linkUrl) || !string.IsNullOrWhiteSpace(offer.imageUrl));
        }
    }
}
