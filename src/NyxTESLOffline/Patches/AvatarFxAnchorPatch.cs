using System;
using HarmonyLib;
using SPTESL;

namespace NyxTESLOffline.Patches
{
    // A portrait is part of the board, not a lane card that an FX holder may
    // reparent and translate. Keep the target reference for impact particles,
    // damage feedback and variant selection; exclude it only from reparenting.
    [HarmonyPatch(typeof(VisualDirector), "SetObjectRefParents")]
    internal static class AvatarFxAnchorPatch
    {
        internal struct SavedAvatars
        {
            internal Card3D Source;
            internal Card3D Target;
        }

        private static bool IsAvatar(Card3D card)
        {
            return !Object.ReferenceEquals(card, null) &&
                !Object.ReferenceEquals(card.ClientCard, null) &&
                card.ClientCard.GameZoneName == GameZoneName.AvatarBoard;
        }

        [HarmonyPrefix]
        private static void Prefix(ref Card3D ____sourceObjectRef,
            ref Card3D ____targetObjectRef, out SavedAvatars __state)
        {
            __state = new SavedAvatars();
            if (Object.ReferenceEquals(Plugin.ActiveLocalMatch, null))
                return;
            if (IsAvatar(____sourceObjectRef))
            {
                __state.Source = ____sourceObjectRef;
                ____sourceObjectRef = null;
            }
            if (IsAvatar(____targetObjectRef))
            {
                __state.Target = ____targetObjectRef;
                ____targetObjectRef = null;
            }
        }

        [HarmonyPostfix]
        private static void Postfix(ref Card3D ____sourceObjectRef,
            ref Card3D ____targetObjectRef, SavedAvatars __state)
        {
            if (!Object.ReferenceEquals(__state.Source, null))
                ____sourceObjectRef = __state.Source;
            if (!Object.ReferenceEquals(__state.Target, null))
                ____targetObjectRef = __state.Target;
            if (!Object.ReferenceEquals(__state.Source, null) ||
                !Object.ReferenceEquals(__state.Target, null))
                Plugin.Log("avatar FX anchor retained: source=" +
                    (Object.ReferenceEquals(__state.Source, null) ? 0 : __state.Source.NetUid.Id)
                    + " target=" + (Object.ReferenceEquals(__state.Target, null) ? 0 : __state.Target.NetUid.Id));
        }
    }

    [HarmonyPatch(typeof(SequenceDataLookup), "GetMainAsset")]
    internal static class RetailFxAssetTracePatch
    {
        [HarmonyPostfix]
        private static void Postfix(CardFxDefinition definition,
            SequenceDataLookup.FxDefinitionTestResult __result)
        {
            if (!Object.ReferenceEquals(Plugin.ActiveLocalMatch, null) &&
                !Object.ReferenceEquals(definition, null))
                Plugin.Log("retail FX selected: definition=" + definition.Name
                    + " asset=" + __result.Asset);
        }
    }
}
