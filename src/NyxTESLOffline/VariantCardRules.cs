using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using SPTESL;
namespace NyxTESLOffline
{
    // Premium is ServerCard.Premium, not a different gameplay identity.
    internal static class VariantCardRules
    {
        static readonly string[] Fields={"TriggeredEffects","ActionPlayInformation","Auras","PlayRequirements","BetrayRequirements","AttackRequirements","StrongHintRequirements","ActivatedAbility","DefaultRareAttributes","UpgradesInto","DoubleCardReferences","DynamicTextAssignments","MobilizeRecruitId","HuntTypes"};
        static GameConfiguration CachedConfig;
        static readonly Dictionary<int,int> CanonicalCache=new Dictionary<int,int>();
        internal static int CanonicalId(int id)
        {
            var config=GameManager.Instance.Config;
            if(!Object.ReferenceEquals(config,CachedConfig)){CanonicalCache.Clear();CachedConfig=config;}
            int result;if(CanonicalCache.TryGetValue(id,out result))return result;
            int requested=id;var seen=new HashSet<int>();
            while(config!=null && seen.Add(id)) {
                var c=config.FindCardById(id);if(c==null)break;
                int next=c.AlternateOfCard.resourceId;if(next==0 || next==id || config.FindCardById(next)==null)break;
                id=next;
            }
            CanonicalCache[requested]=id;
            return id;
        }
        internal static bool HasRules(Card c)
        {return c.TriggeredEffects.Count>0 || c.ActionPlayInformation.Count>0 || c.Auras.Count>0 || c.ActivatedAbility.IsValidAbility();}
        internal static void Apply(GameConfiguration config)
        {
            int count=0;
            // A populated alternate may be the only available original composition.
            foreach(var set in config.CardCollection)foreach(var c in set.Cards) {
                var original=config.FindCardById(CanonicalId(c.ResourceId.Id));
                if(original!=null && original!=c && !HasRules(original) && HasRules(c))CopyMissing(c,original);
            }
            foreach(var set in config.CardCollection)foreach(var c in set.Cards) {
                var original=config.FindCardById(CanonicalId(c.ResourceId.Id));
                if(original!=null && original!=c && CopyMissing(original,c))count++;
            }
            Plugin.Log("variant gameplay inheritance: updated="+count+"; premium shares the same card definition");
        }
        static bool Empty(object value)
        {
            if(value==null)return true;var list=value as IList;if(list!=null)return list.Count==0;
            if(value is ActivatedAbility)return !((ActivatedAbility)value).IsValidAbility();
            var fields=value.GetType().GetFields(BindingFlags.Instance|BindingFlags.Public);
            if(fields.Length==0)return false;
            foreach(var f in fields) {var v=f.GetValue(value);if(v is IList && ((IList)v).Count>0)return false;if(v is int && (int)v!=0 && (int)v!=-1)return false;}
            return true;
        }
        static bool CopyMissing(Card source,Card target)
        {
            bool changed=false;
            foreach(string name in Fields) {
                var f=typeof(Card).GetField(name);if(f==null)continue;
                var from=f.GetValue(source);if(Empty(from) || !Empty(f.GetValue(target)))continue;
                var list=from as IList;
                if(list!=null){var copy=(IList)Activator.CreateInstance(f.FieldType);foreach(var entry in list)copy.Add(entry);from=copy;}
                f.SetValue(target,from);changed=true;
            }
            // Existing alternate-specific resolvers always win.
            foreach(var fx in source.CardFxRelations)if(!target.CardFxRelations.Exists(x=>x.FXAction==fx.FXAction && x.EffectTriggerType==fx.EffectTriggerType))target.CardFxRelations.Add(fx);
            return changed;
        }
    }
}
