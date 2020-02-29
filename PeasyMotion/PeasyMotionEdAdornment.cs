﻿//#define MEASUREEXECTIME

using System;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Operations;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Collections.Generic;
using System.Windows;
using Microsoft.VisualStudio.Utilities;
using Microsoft.VisualStudio.PlatformUI;

namespace PeasyMotion
{ 
    /// <summary>
    /// PeasyMotionEdAdornment places red boxes behind all the "a"s in the editor window
    /// </summary>
    internal sealed class PeasyMotionEdAdornment
    {
        private ITextStructureNavigator textStructureNavigator{ get; set; }
            /// <summary>
            /// The layer of the adornment.
            /// </summary>
        private readonly IAdornmentLayer layer;

        /// <summary>
        /// Text view where the adornment is created.
        /// </summary>
        private readonly IWpfTextView view;
        private VsSettings vsSettings;
        private JumpLabelUserControl.CachedSetupParams jumpLabelCachedSetupParams = new JumpLabelUserControl.CachedSetupParams();

        private struct Jump
        {
            public SnapshotSpan span;
            public string label;
            public JumpLabelUserControl labelAdornment;
            public bool nextIsControl;
        };
        private struct JumpWord
        {
            public int distanceToCursor;
            public Rect adornmentBounds;
            public SnapshotSpan span;
            public string text;
            public bool nextIsControl;
        };

        private List<Jump> currentJumps = new List<Jump>();
        public bool anyJumpsAvailable() => currentJumps.Count > 0;

        const string jumpLabelKeyArray = "asdghklqwertyuiopzxcvbnmfj;";

        public PeasyMotionEdAdornment() { // just for listener
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PeasyMotionEdAdornment"/> class.
        /// </summary>
        /// <param name="view">Text view to create the adornment for</param>
        public PeasyMotionEdAdornment(IWpfTextView view, ITextStructureNavigator textStructNav)
        {
            var jumpLabelAssignmentAlgorithm = GeneralOptions.Instance.jumpLabelAssignmentAlgorithm;
            var caretPositionSensivity = Math.Min(Int32.MaxValue >> 2, Math.Abs(GeneralOptions.Instance.caretPositionSensivity));

            this.layer = view.GetAdornmentLayer("PeasyMotionEdAdornment");

            var watch1 = System.Diagnostics.Stopwatch.StartNew();

            this.textStructureNavigator = textStructNav;

            this.view = view;
            this.view.LayoutChanged += this.OnLayoutChanged;

            this.vsSettings = VsSettings.GetOrCreate(view);
            // subscribe to fmt updates, so user can tune color faster if PeasyMotion was invoked
            this.vsSettings.PropertyChanged += this.OnFormattingPropertyChanged;

            this.jumpLabelCachedSetupParams.fontRenderingEmSize = this.view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            this.jumpLabelCachedSetupParams.typeface = this.view.FormattedLineSource.DefaultTextProperties.Typeface;
            this.jumpLabelCachedSetupParams.labelFg = this.vsSettings.JumpLabelFirstMotionForegroundColor;
            this.jumpLabelCachedSetupParams.labelBg = this.vsSettings.JumpLabelFirstMotionBackgroundColor;
            this.jumpLabelCachedSetupParams.labelFinalMotionFg = this.vsSettings.JumpLabelFinalMotionForegroundColor;
            this.jumpLabelCachedSetupParams.labelFinalMotionBg = this.vsSettings.JumpLabelFinalMotionBackgroundColor;
            this.jumpLabelCachedSetupParams.Freeze();

            var jumpWords = new List<JumpWord>();

            int currentTextPos = this.view.TextViewLines.FirstVisibleLine.Start;
            int lastTextPos = this.view.TextViewLines.LastVisibleLine.End;

            var cursorSnapshotPt = this.view.Caret.Position.BufferPosition;
            int cursorIndex = 0;
            if (JumpLabelAssignmentAlgorithm.CaretRelative == jumpLabelAssignmentAlgorithm)
            {
                cursorIndex = cursorSnapshotPt.Position;
                if ((cursorIndex < currentTextPos) || (cursorIndex > lastTextPos))
                {
                    cursorSnapshotPt = this.view.TextSnapshot.GetLineFromPosition(currentTextPos + (lastTextPos - currentTextPos) / 2).Start;
                    cursorIndex = cursorSnapshotPt.Position;
                }

                // bin caret to virtual segments accroding to sensivity option, with sensivity=0 does nothing
                int dc = caretPositionSensivity + 1;
                cursorIndex = (cursorIndex / dc) * dc + (dc / 2);
            }

            // collect words and required properties in visible text
            char prevChar = '\0';
            var startPoint = this.view.TextViewLines.FirstVisibleLine.Start;
            var endPoint = this.view.TextViewLines.LastVisibleLine.End;
            var snapshot = startPoint.Snapshot;
            int lastJumpPos = -1;
            bool prevIsSeparator = Char.IsSeparator(prevChar);
            bool prevIsPunctuation = Char.IsPunctuation(prevChar);
            bool prevIsLetterOrDigit = Char.IsLetterOrDigit(prevChar);
            bool prevIsControl = Char.IsControl(prevChar);
            var lastPosition = Math.Max(endPoint.Position-1, 0);
            var currentPoint = new SnapshotPoint(snapshot, startPoint.Position);
            var nextPoint = new SnapshotPoint(snapshot, Math.Min(startPoint.Position+1, lastPosition)); 
            int i = startPoint.Position;
            if (startPoint.Position == lastPosition) {
                i = lastPosition + 99; // just skip the loop. noob way :D 
            }
            for (; i <= lastPosition; i++)
            {
                var ch = currentPoint.GetChar();
                var nextCh = nextPoint.GetChar();
                bool curIsSeparator = Char.IsSeparator(ch);
                bool curIsPunctuation = Char.IsPunctuation(ch);
                bool curIsLetterOrDigit = Char.IsLetterOrDigit(ch);
                bool curIsControl = Char.IsControl(ch);
                bool nextIsControl_ = Char.IsControl(nextCh) && (!curIsControl);
                if (//TODO: anything faster and simpler ? will regex be faster?
                    (
                    (i == 0) || // file start
                        ((prevIsControl || prevIsPunctuation || prevIsSeparator) && curIsLetterOrDigit) || // word begining?
                        ((prevIsLetterOrDigit || prevIsSeparator || prevIsControl || Char.IsWhiteSpace(prevChar)) && curIsPunctuation) // { } [] etc
                        )
                    &&
                    ((lastJumpPos + 2) < i) // make sure there is a lil bit of space between adornments
                    )
                {
                    SnapshotSpan firstCharSpan = new SnapshotSpan(this.view.TextSnapshot, Span.FromBounds(i, i + 1));
                    Geometry geometry = this.view.TextViewLines.GetTextMarkerGeometry(firstCharSpan);
                    if (geometry != null)
                    {
                        var jw = new JumpWord()
                        {
                            distanceToCursor = Math.Abs(i - cursorIndex),
                            adornmentBounds = geometry.Bounds,
                            span = firstCharSpan,
                            text = null,
                            nextIsControl = nextIsControl_
                        };
                        jumpWords.Add(jw);
                        lastJumpPos = i;
                    }
                }
                prevChar = ch;
                prevIsSeparator = curIsSeparator;
                prevIsPunctuation = curIsPunctuation;
                prevIsLetterOrDigit = curIsLetterOrDigit;
                prevIsControl = curIsControl;

                currentPoint = nextPoint;
                nextPoint = new SnapshotPoint(snapshot, Math.Min(i+2, lastPosition));
            }
#if false
            for (int i = 0; i < 256; i++) {
                Debug.WriteLine("Char.IsSeparator(" + ((char)i) + " = " + Char.IsLowSurrogate((char)i));
                Debug.WriteLine("Char.IsControl(" + ((char)i) + " = " + Char.IsControl((char)i));
                Debug.WriteLine("Char.IsDigit(" + ((char)i) + " = " + Char.IsDigit((char)i));
                Debug.WriteLine("Char.IsHighSurrogate(" + ((char)i) + " = " + Char.IsHighSurrogate((char)i));
                Debug.WriteLine("Char.IsLetterOrDigit(" + ((char)i) + " = " + Char.IsLetterOrDigit((char)i));
                Debug.WriteLine("Char.IsLowSurrogate(" + ((char)i) + " = " + Char.IsLowSurrogate((char)i));
                Debug.WriteLine("Char.IsNumber(" + ((char)i) + " = " + Char.IsNumber((char)i));
                Debug.WriteLine("Char.IsPunctuation(" + ((char)i) + " = " + Char.IsPunctuation((char)i));
                Debug.WriteLine("Char.IsSeparator(" + ((char)i) + " = " + Char.IsSeparator((char)i));
                Debug.WriteLine("Char.IsSymbol(" + ((char)i) + " = " + Char.IsSymbol((char)i));
                Debug.WriteLine("-----");
            }
#endif
            /* // too slow
            do
            {
                var word_span = GetNextWord(new SnapshotPoint(this.view.TextSnapshot, currentTextPos));
                if (word_span.HasValue && (!word_span.Value.Contains(cursorSnapshotPt)))
                {
                    var word = this.view.TextSnapshot.GetText(word_span.Value);
                    if (Char.IsLetter(word[0]) || Char.IsNumber(word[0]) )
                    {
                        //Debug.WriteLine(word);
                        int charIndex = word_span.Value.Start;
                        SnapshotSpan firstCharSpan = new SnapshotSpan(this.view.TextSnapshot, Span.FromBounds(charIndex, charIndex + 1));
                        Geometry geometry = this.view.TextViewLines.GetTextMarkerGeometry(firstCharSpan);
                        if (geometry != null)
                        {
                            var jw = new JumpWord()
                            {
                                distanceToCursor = Math.Abs(charIndex - cursorIndex),
                                adornmentBounds = geometry.Bounds,
                                span = firstCharSpan,
                                text = word
                            };
                            jumpWords.Add(jw);
                        }
                    }
                    currentTextPos = word_span.Value.End;
                }
                else
                {
                    currentTextPos++;
                }
            } while (currentTextPos < lastTextPos);
            */

#if MEASUREEXECTIME
            watch1.Stop();
            Debug.WriteLine($"PeasyMotion Adornment find words: {watch1.ElapsedMilliseconds} ms");
            var watch2 = System.Diagnostics.Stopwatch.StartNew();
#endif
            if (JumpLabelAssignmentAlgorithm.CaretRelative == jumpLabelAssignmentAlgorithm)
            {
                // sort jump words from closest to cursor to farthest
                jumpWords.Sort((a, b) => -a.distanceToCursor.CompareTo(b.distanceToCursor));
            }
#if MEASUREEXECTIME
            watch2.Stop();
            Debug.WriteLine($"PeasyMotion Adornment sort words: {watch2.ElapsedMilliseconds} ms");
            var watch3 = System.Diagnostics.Stopwatch.StartNew();
#endif

            _ = computeGroups(0, jumpWords.Count - 1, jumpLabelKeyArray, null, jumpWords);

#if MEASUREEXECTIME
            watch3.Stop();
            Debug.WriteLine($"PeasyMotion Adornments create: {adornmentCreateStopwatch.ElapsedMilliseconds} ms");
            adornmentCreateStopwatch = null;
            Debug.WriteLine($"PeasyMotion Adornments UI Elem create: {createAdornmentUIElem.ElapsedMilliseconds} ms");
            createAdornmentUIElem = null;
            Debug.WriteLine($"PeasyMotion Adornments group&create: {watch3.ElapsedMilliseconds} ms");
            Debug.WriteLine($"PeasyMotion Adornment total jump labels - {jumpWords.Count}");
#endif
        }

        ~PeasyMotionEdAdornment()
        {
            if (view != null) {
                this.vsSettings.PropertyChanged -= this.OnFormattingPropertyChanged;
            }
        }

        public void Dispose()
        {
            this.vsSettings.PropertyChanged -= this.OnFormattingPropertyChanged;
        }

        public void OnFormattingPropertyChanged(object o, System.ComponentModel.PropertyChangedEventArgs prop)
        {
            var val = vsSettings[prop.PropertyName];
            switch (prop.PropertyName)
            {
            case nameof(VsSettings.JumpLabelFirstMotionForegroundColor):
                {
                    var brush = val as SolidColorBrush;
                    foreach(var j in currentJumps) { if ((j.labelAdornment.Content as string).Length>1) j.labelAdornment.Foreground = brush; }
                }
                break;
            case nameof(VsSettings.JumpLabelFirstMotionBackgroundColor):
                {
                    var brush = val as SolidColorBrush;
                    foreach(var j in currentJumps) { if ((j.labelAdornment.Content as string).Length>1) j.labelAdornment.Background = brush; }
                }
                break;
            case nameof(VsSettings.JumpLabelFinalMotionForegroundColor):
                {
                    var brush = val as SolidColorBrush;
                    foreach(var j in currentJumps) { if ((j.labelAdornment.Content as string).Length==1) j.labelAdornment.Foreground = brush; }
                }
                break;
            case nameof(VsSettings.JumpLabelFinalMotionBackgroundColor):
                {
                    var brush = val as SolidColorBrush;
                    foreach(var j in currentJumps) { if ((j.labelAdornment.Content as string).Length==1) j.labelAdornment.Background = brush; }
                }
                break;
            }
        }

        public struct JumpNode
        {
            public int jumpWordIndex;
            public Dictionary<char, JumpNode> childrenNodes;
        };

#if MEASUREEXECTIME
        private Stopwatch adornmentCreateStopwatch = null;
        private Stopwatch createAdornmentUIElem = null;
#endif

        private Dictionary<char, JumpNode> computeGroups(int wordStartIndex, int wordEndIndex, string keys0, string prefix, List<JumpWord> jumpWords)
        { 
            // SC-Tree algorithm from vim-easymotion script with minor changes
            var wordCount = wordEndIndex - wordStartIndex + 1;
            var keyCount = keys0.Length;

            Dictionary<char, JumpNode> groups = new Dictionary<char, JumpNode>();

            var keys = Reverse(keys0);

            var keyCounts = new int[keyCount];
            var keyCountsKeys = new Dictionary<char, int>(keyCount);
            var j = 0;
            foreach(char key in keys)
            {
                keyCounts[j] = 0;
                keyCountsKeys[key] = j;
                j++;
            }

            var targetsLeft = wordCount;
            var level = 0;
            var i = 0;

            while (targetsLeft > 0)
            {
                var childrenCount = level == 0 ? 1 : keyCount - 1;
                foreach(char key in keys)
                {
                    keyCounts[keyCountsKeys[key]] += childrenCount;
                    targetsLeft -= childrenCount;
                    if (targetsLeft <= 0)
                    {
                        keyCounts[keyCountsKeys[key]] += targetsLeft;
                        break;
                    }
                    i += 1;

                }
                level += 1;
            }

            var k = 0;
            var keyIndex = 0;
            foreach (int KeyCount2 in keyCounts)
            {
                if (KeyCount2 > 1)
                {
                    groups[keys0[keyIndex]] = new JumpNode()
                    {
                        jumpWordIndex = -1,
                        childrenNodes = computeGroups(wordStartIndex + k, wordStartIndex + k + KeyCount2 - 1 - 1, keys0, 
                            prefix!=null ? (prefix + keys0[keyIndex]) : ""+keys0[keyIndex], jumpWords )
                    };
                }
                else if (KeyCount2 == 1)
                {
                    groups[keys0[keyIndex]] = new JumpNode()
                    {
                        jumpWordIndex = wordStartIndex + k,
                        childrenNodes = null
                    };
                    var jw = jumpWords[wordStartIndex + k];
                    string jumpLabel = prefix + keys0[keyIndex];

#if MEASUREEXECTIME
                    if (createAdornmentUIElem == null)
                    {
                        createAdornmentUIElem = Stopwatch.StartNew();
                    }
                    else
                    {
                        createAdornmentUIElem.Start();
                    }
#endif
                    var adornment = JumpLabelUserControl.GetFreeUserControl();
                    adornment.setup(jumpLabel, jw.adornmentBounds, this.jumpLabelCachedSetupParams);
                    
#if MEASUREEXECTIME
                    createAdornmentUIElem.Stop();
#endif

#if MEASUREEXECTIME
                    if (adornmentCreateStopwatch == null)
                    {
                        adornmentCreateStopwatch = Stopwatch.StartNew();
                    }
                    else
                    {
                        adornmentCreateStopwatch.Start();
                    }
#endif
                    this.layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, jw.span, null, adornment, JumpLabelAdornmentRemovedCallback);
#if MEASUREEXECTIME
                    adornmentCreateStopwatch.Stop();
#endif

                    //Debug.WriteLine(jw.text + " -> |" + jumpLabel + "|");
                    var cj = new Jump() { span = jw.span, label = jumpLabel, 
                        labelAdornment = adornment, nextIsControl = jw.nextIsControl };
                    currentJumps.Add(cj);
                }
                else
                {
                    continue;
                }
                keyIndex += 1;
                k += KeyCount2;
            }

            return groups;
        }

        public void JumpLabelAdornmentRemovedCallback(object _, UIElement element)
        {
            JumpLabelUserControl.ReleaseUserControl(element as JumpLabelUserControl);
        }

        public static string Reverse( string s )
        {
            char[] charArray = s.ToCharArray();
            Array.Reverse( charArray );
            return new string( charArray );
        }
        internal SnapshotSpan? GetNextWord(SnapshotPoint position)
        {
            var word = this.textStructureNavigator.GetExtentOfWord(position);
            while (!word.IsSignificant && !word.Span.IsEmpty)
            {
                SnapshotSpan previousWordSpan = word.Span;
                word = this.textStructureNavigator.GetExtentOfWord(word.Span.End);
                if (word.Span == previousWordSpan)
                {
                    return null;
                }
            }

            return word.IsSignificant ? new SnapshotSpan?(word.Span) : null;
        }

        /// <summary>
        /// Handles whenever the text displayed in the view changes by adding the adornment to any reformatted lines
        /// </summary>
        /// <remarks><para>This event is raised whenever the rendered text displayed in the <see cref="ITextView"/> changes.</para>
        /// <para>It is raised whenever the view does a layout (which happens when DisplayTextLineContainingBufferPosition is called or in response to text or classification changes).</para>
        /// <para>It is also raised whenever the view scrolls horizontally or when its size changes.</para>
        /// </remarks>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event arguments.</param>
        internal void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
        }

        internal void Reset()
        {
            this.layer.RemoveAllAdornments();
            this.currentJumps.Clear();
        }

        internal (bool finalJump, bool nextCharIsControlChar) JumpTo(string label)
        {
            int idx = currentJumps.FindIndex(0, j => j.label == label);
            if (-1 < idx)
            {
                var j = currentJumps[idx];
                this.view.Caret.MoveTo(j.span.Start);
                return (true, j.nextIsControl);
            } 
            else
            {
                currentJumps.RemoveAll(
                    delegate (Jump j)
                    {
                        bool b = !j.label.StartsWith(label, StringComparison.InvariantCulture);
                        if (b)
                        {
                            this.layer.RemoveAdornment(j.labelAdornment);
                        }
                        return b;
                    }
                );

                foreach(Jump j in currentJumps)
                {
                    j.labelAdornment.UpdateView(j.label.Substring(label.Length), this.jumpLabelCachedSetupParams);
                }
            }
            return (false, false);
        }
    }
}
