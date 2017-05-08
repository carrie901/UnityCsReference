// Unity C# reference source
// Copyright (c) Unity Technologies. For terms of use, see
// https://unity3d.com/legal/licenses/Unity_Reference_Only_License

using System;
using System.Collections.Generic;
using UnityEngine.StyleSheets;

namespace UnityEngine.Experimental.UIElements.StyleSheets
{
    struct RuleMatcher
    {
        public StyleSheet sheet;
        public StyleComplexSelector complexSelector;
        public int simpleSelectorIndex;
        public int depth;
    }

    internal class StyleContext
    {
        public float currentPixelsPerPoint { get; set; }

        List<RuleMatcher> m_Matchers;
        List<RuleRef> m_MatchedRules;
        VisualContainer m_VisualTree;

        struct RuleRef
        {
            public StyleComplexSelector selector;
            public StyleSheet sheet;
        }

        // hash of a set of rules to a resolved style
        // the same set of rules will give the same resolved style, caching the hash of the matching rules before
        // resolving styles allows to skip the resolve part when an existing resolved style already exists
        private static Dictionary<Int64, VisualElementStyles> s_StyleCache = new Dictionary<Int64, VisualElementStyles>();

        public StyleContext(VisualContainer tree)
        {
            m_VisualTree = tree;
            m_Matchers = new List<RuleMatcher>(capacity: 0);
            m_MatchedRules = new List<RuleRef>(capacity: 0);
        }

        void AddMatchersFromSheet(IEnumerable<StyleSheet> styleSheets)
        {
            foreach (var styleSheetData in styleSheets)
            {
                PushStyleSheet(styleSheetData);
            }
        }

        internal void GetMatchersFor(VisualElement element, List<RuleMatcher> ruleMatchers, List<StyleSheet> stylesheets)
        {
            List<VisualElement> hierarchy = new List<VisualElement>();
            hierarchy.Add(element);
            VisualContainer cursor = element as VisualContainer;
            if (cursor == null)
                cursor = element.parent;
            while (cursor != null)
            {
                if (cursor.styleSheets != null)
                {
                    stylesheets.AddRange(cursor.styleSheets);
                    AddMatchersFromSheet(cursor.styleSheets);
                }
                hierarchy.Add(cursor);
                cursor = cursor.parent;
            }
            int depth = 0;
            for (int i = hierarchy.Count - 1; i >= 0; i--)
            {
                GetMatchersFor(hierarchy, i, depth++, ruleMatchers);
            }
            m_Matchers.Clear();
        }

        private void GetMatchersFor(List<VisualElement> elements, int idx, int depth, List<RuleMatcher> ruleMatchers)
        {
            var element = elements[idx];
            int count = m_Matchers.Count; // changes while we iterate so save

            for (int i = 0; i < count; i++)
            {
                RuleMatcher matcher = m_Matchers[i];

                if (matcher.depth < depth || // ignore matchers that don't apply to this depth
                    !Match(element, ref matcher))
                {
                    continue;
                }

                StyleSelector[] selectors = matcher.complexSelector.selectors;
                int nextIndex = matcher.simpleSelectorIndex + 1;
                int selectorsCount = selectors.Length;
                // if this sub selector in the complex selector is not the last
                // we create a new matcher for the next element
                // will stay in the list of matchers for as long as we visit descendents
                if (nextIndex < selectorsCount)
                {
                    RuleMatcher copy = new RuleMatcher()
                    {
                        complexSelector = matcher.complexSelector,
                        depth = selectors[nextIndex].previousRelationship == StyleSelectorRelationship.Child
                            ? depth + 1
                            : int.MaxValue,
                        simpleSelectorIndex = nextIndex,
                        sheet = matcher.sheet
                    };

                    m_Matchers.Add(copy);
                }
                // Otherwise we add the rule as matching this element
                else if (idx == 0)
                {
                    ruleMatchers.Add(matcher);
                }
            }
        }

        void PushStyleSheet(StyleSheet styleSheetData)
        {
            var complexSelectors = styleSheetData.complexSelectors;
            // To avoid excessive re-allocations, just resize the list right now
            int futureSize = m_Matchers.Count + complexSelectors.Length;
            m_Matchers.Capacity = Math.Max(m_Matchers.Capacity, futureSize);

            for (int i = 0; i < complexSelectors.Length; i++)
            {
                StyleComplexSelector complexSelector = complexSelectors[i];
                // For every complex selector, push a matcher for first sub selector
                m_Matchers.Add(new RuleMatcher()
                {
                    sheet = styleSheetData,
                    complexSelector = complexSelector,
                    simpleSelectorIndex = 0,
                    depth = int.MaxValue
                });
            }
        }

        public void DirtyStyleSheets()
        {
            PropagateDirtyStyleSheets(m_VisualTree);
        }

        static void PropagateDirtyStyleSheets(VisualElement e)
        {
            var c = e as VisualContainer;
            if (c != null)
            {
                if (c.styleSheets != null)
                    c.LoadStyleSheetsFromPaths();

                foreach (var child in c)
                {
                    PropagateDirtyStyleSheets(child);
                }
            }
        }

        public void ApplyStyles(VisualContainer subTree)
        {
            Debug.Assert(subTree.panel != null);
            UpdateStyles(subTree, 0);
            m_Matchers.Clear();
        }

        public void ApplyStyles()
        {
            ApplyStyles(m_VisualTree);
        }

        void UpdateStyles(VisualElement element, int depth)
        {
            // if subtree is up to date skip
            if (!element.IsDirty(ChangeType.Styles)
                && !element.IsDirty(ChangeType.StylesPath))
            {
                return;
            }

            var container = element as VisualContainer;
            int originalCount = m_Matchers.Count;
            if (container != null && container.styleSheets != null)
            {
                AddMatchersFromSheet(container.styleSheets);
            }

            VisualElementStyles resolvedStyles;
            string elementTypeName = element.fullTypeName;

            Int64 matchingRulesHash = elementTypeName.GetHashCode();
            // Let current DPI contribute to the hash so cache is invalidated when this changes
            matchingRulesHash = (matchingRulesHash * 397) ^ currentPixelsPerPoint.GetHashCode();
            m_MatchedRules.Clear();

            int count = m_Matchers.Count; // changes while we iterate so save

            for (int j = 0; j < count; j++)
            {
                RuleMatcher matcher = m_Matchers[j];

                if (matcher.depth < depth || // ignore matchers that don't apply to this depth
                    !Match(element, ref matcher))
                {
                    continue;
                }

                StyleSelector[] selectors = matcher.complexSelector.selectors;
                int nextIndex = matcher.simpleSelectorIndex + 1;
                int selectorsCount = selectors.Length;
                // if this sub selector in the complex selector is not the last
                // we create a new matcher for the next element
                // will stay in the list of matchers for as long as we visit descendents
                if (nextIndex < selectorsCount)
                {
                    RuleMatcher copy = new RuleMatcher()
                    {
                        complexSelector = matcher.complexSelector,
                        depth = selectors[nextIndex].previousRelationship == StyleSelectorRelationship.Child ? depth + 1 : int.MaxValue,
                        simpleSelectorIndex = nextIndex,
                        sheet = matcher.sheet
                    };

                    m_Matchers.Add(copy);
                }
                // Otherwise we add the rule as matching this element
                else
                {
                    StyleRule rule = matcher.complexSelector.rule;
                    int specificity = matcher.complexSelector.specificity;
                    matchingRulesHash = (matchingRulesHash * 397) ^ rule.GetHashCode();
                    matchingRulesHash = (matchingRulesHash * 397) ^ specificity;
                    m_MatchedRules.Add(new RuleRef { selector = matcher.complexSelector, sheet = matcher.sheet });
                }
            }

            if (s_StyleCache.TryGetValue(matchingRulesHash, out resolvedStyles))
            {
                // we should not new it in StyleTree
                element.SetSharedStyles(resolvedStyles);
            }
            else
            {
                resolvedStyles = new VisualElementStyles(isShared: true);

                for (int i = 0, ruleCount = m_MatchedRules.Count; i < ruleCount; i++)
                {
                    RuleRef ruleRef = m_MatchedRules[i];
                    StylePropertyID[] propertyIDs = StyleSheetCache.GetPropertyIDs(ruleRef.sheet, ruleRef.selector.ruleIndex);
                    resolvedStyles.ApplyRule(ruleRef.sheet, ruleRef.selector.specificity, ruleRef.selector.rule, propertyIDs, m_VisualTree.elementPanel.loadResourceFunc);
                }

                s_StyleCache[matchingRulesHash] = resolvedStyles;

                element.SetSharedStyles(resolvedStyles);
            }

            if (container != null)
            {
                for (int i = 0; i < container.childrenCount; i++)
                {
                    var child = container.GetChildAt(i);
                    UpdateStyles(child, depth + 1);
                }
            }

            // Remove all matchers that we could possibly have added at this level of recursion
            if (m_Matchers.Count > originalCount)
            {
                m_Matchers.RemoveRange(originalCount, m_Matchers.Count - originalCount);
            }
        }

        static bool Match(VisualElement element, ref RuleMatcher matcher)
        {
            bool match = true;
            StyleSelector selector = matcher.complexSelector.selectors[matcher.simpleSelectorIndex];
            int count = selector.parts.Length;
            for (int i = 0; i < count && match; i++)
            {
                switch (selector.parts[i].type)
                {
                    case StyleSelectorType.Wildcard:
                        break;
                    case StyleSelectorType.Class:
                        match = element.ClassListContains(selector.parts[i].value);
                        break;
                    case StyleSelectorType.ID:
                        match = (element.name == selector.parts[i].value);
                        break;
                    case StyleSelectorType.Type:
                        match = (element.typeName == selector.parts[i].value);
                        break;
                    case StyleSelectorType.PseudoClass:
                        int pseudoStates = (int)element.pseudoStates;
                        match = (selector.pseudoStateMask & pseudoStates) == selector.pseudoStateMask;
                        match &= (selector.negatedPseudoStateMask & ~pseudoStates) == selector.negatedPseudoStateMask;
                        break;
                    default: // ignore, all errors should have been warned before hand
                        match = false;
                        break;
                }
            }
            return match;
        }
    }
}
