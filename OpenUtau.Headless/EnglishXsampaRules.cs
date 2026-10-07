// English alias rules adapted from OpenUtau.Plugin.Builtin.EnXSampaPhonemizer.
// Kept independent of desktop singer objects and neural G2P dependencies.
using System.Text.RegularExpressions;

namespace OpenUtau.Headless;

internal sealed class EnglishXsampaRules {

        private readonly Func<string, int, bool> lookup;
        private readonly string[] tails = ["-", "R"];
        private readonly string[] affricate = ["tS", "dZ"];
        private readonly string[] liquid = ["l", "r", "r\\"];
        private readonly string[] semivowel = ["j", "w"];
        private readonly HashSet<string> runtimeGlides = [];
        private void glides(string alias) => runtimeGlides.Add(alias);
        internal bool IsGlide(string alias) => runtimeGlides.Contains(alias);
        private string[] vowels = [], consonants = [];
        private Dictionary<string, string> dictionaryReplacements = [];
        private readonly Dictionary<string, string[]> diphthongSplits = [];
        private readonly Dictionary<string, string> diphthongTails = new() { ["aI"] = "j", ["eI"] = "j", ["OI"] = "j", ["aU"] = "w", ["oU"] = "w" };
        public bool IsVowel(string phone) => vowels.Contains(phone);
        private bool HasOto(string alias, int tone) => !string.IsNullOrEmpty(alias) && lookup(alias, tone);
        private string ReplacePhoneme(string phone, int tone) => dictionaryReplacements.GetValueOrDefault(phone, phone);
        private static bool CanMakeAliasExtension(Syllable value) => value.canAliasBeExtended && value.prevV == value.v && value.cc.Length == 0;
        private bool TryAddPhoneme(List<string> result, int tone, params string[] aliases) {
            foreach (var alias in aliases) if (HasOto(alias, tone)) { result.Add(alias); return true; }
            return false;
        }
        public struct Syllable {
            /// <summary>
            /// vowel from previous syllable for VC
            /// </summary>
            public string prevV;
            /// <summary>
            /// CCs, may be empty
            /// </summary>
            public string[] cc;
            /// <summary>
            /// "base" note. May not actually be vowel, if only consonants way provided
            /// </summary>
            public string v;
            /// <summary>
            /// Start position for vowel. All VC CC goes before this position
            /// </summary>
            public int position;
            /// <summary>
            /// previous note duration, i.e. this is container for VC and CC notes
            /// </summary>
            public int duration;
            /// <summary>
            /// Tone for VC and CC
            /// </summary>
            public int tone;
            /// <summary>
            /// Other phoneme attributes for VC and CC
            /// </summary>
            public object[] attr;
            /// <summary>
            /// tone for base "vowel" phoneme
            /// </summary>
            public int vowelTone;
            /// <summary>
            /// Other phoneme attributes for base "vowel" phoneme
            /// </summary>
            public object[] vowelAttr;

            /// <summary>
            /// 0 if no consonants are taken from previous word;
            /// 1 means first one is taken from previous word, etc.
            /// </summary>
            public int prevWordConsonantsCount;

            /// <summary>
            /// If true, you may use alias extension instead of VV, by putting the phoneme as null if vowels match.
            /// If you do this when canAliasBeExtended == false, the note will produce no phoneme and there will be a break.
            /// Use CanMakeAliasExtension() to pass all checks if alias extension is possible
            /// </summary>
            public bool canAliasBeExtended;

            // Lookahead & Context Properties
            public string nextV;
            public string[] nextCc;
            public string prevBasePhoneme;
            public string nextBasePhoneme;

            public string NextVowel => nextV ?? string.Empty;
            public string[] NextCC => nextCc ?? Array.Empty<string>();
            public string PrevBasePhoneme => prevBasePhoneme ?? string.Empty;
            public string NextBasePhoneme => nextBasePhoneme ?? string.Empty;

            // helpers
            public bool IsStartingV => prevV == "" && cc.Length == 0;
            public bool IsVV => prevV != "" && cc.Length == 0;

            public bool IsStartingCV => prevV == "" && cc.Length > 0;
            public bool IsVCV => prevV != "" && cc.Length > 0;

            public bool IsStartingCVWithOneConsonant => prevV == "" && cc.Length == 1;
            public bool IsVCVWithOneConsonant => prevV != "" && cc.Length == 1;

            public bool IsStartingCVWithMoreThanOneConsonant => prevV == "" && cc.Length > 1;
            public bool IsVCVWithMoreThanOneConsonant => prevV != "" && cc.Length > 1;

            public string[] PreviousWordCc => cc.Take(prevWordConsonantsCount).ToArray();
            public string[] CurrentWordCc => cc.Skip(prevWordConsonantsCount).ToArray();

            public override string ToString() {
                return $"({prevV}) {(cc != null ? string.Join(" ", cc) : "")} {v}";
            }
        }

        public struct Ending {
            /// <summary>
            /// vowel from the last syllable to make VC
            /// </summary>
            public string prevV;
            /// <summary>
            ///  actuall CC at the ending
            /// </summary>
            public string[] cc;
            /// <summary>
            /// The exact lyric/symbol of the tail (e.g., "R", "br", "-", etc.)
            /// </summary>
            public string tail;
            public bool HasTail => !string.IsNullOrEmpty(tail);
            /// <summary>
            /// last note position + duration, all phonemes must be less than this
            /// </summary>
            public int position;
            /// <summary>
            /// last syllable length, max container for all VC CC C-
            /// </summary>
            public int duration;
            /// <summary>
            /// the tone from last syllable, for all ending phonemes
            /// </summary>
            public int tone;
            /// <summary>
            /// Other phoneme attributes from last syllable
            /// </summary>
            public object[] attr;

            // helpers
            public bool IsEndingV => cc.Length == 0;
            public bool IsEndingVC => cc.Length > 0;
            public bool IsEndingVCWithOneConsonant => cc.Length == 1;
            public bool IsEndingVCWithMoreThanOneConsonant => cc.Length > 1;

            public override string ToString() {
                return $"({prevV}) {(cc != null ? string.Join(" ", cc) : "")}";
            }
        }
        public EnglishXsampaRules(Func<string, int, bool> lookup) {
            this.lookup = lookup;
            this.vowels = "a,A,@,{,V,O,aU,aI,E,3,eI,I,i,oU,OI,U,u,Q,Ol,Ql,aUn,e@,eN,IN,e,o,Ar,Qr,Er,Ir,Or,Ur,ir,ur,aIr,aUr,A@,Q@,E@,I@,O@,U@,i@,u@,aI@,aU@,@r,@l,@m,@n,@N,1,e@m,e@n,y,I\\,M,U\\,Y,@\\,@`,3`,A`,Q`,E`,I`,O`,U`,i`,u`,aI`,aU`,},2,3\\,6,7,8,9,&,{~,I~,aU~,VI,VU,@U,ai,ei,Oi,au,ou,Ou,@u,i:,u:,O:,e@0,E~,e~,3r,ar,or,{l,Al,al,El,Il,il,ol,ul,Ul,oUl,@5,u5,O5,A5,E5,I5,i5,mm,nn,ll,NN".Split(',');
            this.consonants = "b,tS,d,D,4,f,g,h,dZ,k,l,m,n,N,p,r,s,S,t,T,v,w,W,j,z,Z,t_},・,_".Split(',');
            this.dictionaryReplacements = ("aa=A;ae={;ah=V;ao=O;aw=aU;ax=@;ay=aI;" +
            "b=b;ch=tS;d=d;dh=D;" + "dx=4;eh=E;el=@l;em=@m;en=@n;eng=@N;er=3;ey=eI;f=f;g=g;hh=h;ih=I;iy=i;jh=dZ;k=k;l=l;m=m;n=n;ng=N;ow=oU;oy=OI;" +
            "p=p;q=・;r=r;s=s;sh=S;t=t;th=T;" + "uh=U;uw=u;v=v;w=w;" + "y=j;z=z;zh=Z").Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        }

        // For banks aliased with VOCALOID-style phonemes
        private readonly Dictionary<string, string> vocaSampa = "A=Q;E=e;i=i:;u=u:;O=O:;3=@r;oU=@U;Ar=Q@;Qr=Q@;Er=e@;er=e@;Ir=I@;ir=I@;i:r=I@;Or=O@;O:r=O@;Ur=U@;ur=U@;u:r=U@".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);
        private bool isVocaSampa = false;

        // For banks with slightly fewer vowels
        private readonly Dictionary<string, string> simpleDelta = "E=e;V=@;o=O".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isSimpleDelta = false;

        // For banks with slightly fewer vowels
        private readonly Dictionary<string, string> CanadianRaising = "VI=aI;VU=aU".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isMissingCanadianRaising = false;

        // For banks with only minimal vowels
        private readonly Dictionary<string, string> miniDelta = "I=i;U=u".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isMiniDelta = false;

        // For Japanese-English combined banks.
        // NOTE: Rather rudimentary by default; a custom dictionary is recommended.
        private readonly Dictionary<string, string> enPlusJa = "j=y;dZ=j;r=r\\;tS=ch".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isEnPlusJa = false;

        // For banks encoded in "true"/more accurate X-SAMPA.
        private readonly Dictionary<string, string> trueXSampa = "r=r\\;3=@`".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isTrueXSampa = false;

        // For banks using Salem's reclist.
        private readonly Dictionary<string, string> salemList = "3=@r;Ar=ar;aIr=ar;aUr=ar;Or=or;aIl=al".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isSalemList = false;

        // Velar nasal fallback
        private readonly Dictionary<string, string> velarNasalFallback = "N g=n g;N k=n k".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isVelarNasalFallback = false;

        // For Kasane Teto's missing sample
        private readonly Dictionary<string, string> tetoException = "V=@".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isTetoException = false;

        // For dark L vowels
        private readonly Dictionary<string, string> darkLVowel = "@l=@5,ul=u5,Ol=O5,Al=A5,El=E5,Il=I5,il=i5,".Split(';')
                .Select(entry => entry.Split('='))
                .Where(parts => parts.Length == 2)
                .Where(parts => parts[0] != parts[1])
                .ToDictionary(parts => parts[0], parts => parts[1]);

        private bool isDarkLVowel = false;

        private readonly Dictionary<string, string> vvExceptions =
            new Dictionary<string, string>() {
                {"aI","j"},
                {"eI","j"},
                {"OI","j"},
                {"aU","w"},
                {"oU","w"},
                {"VI","j"},
                {"VU","w"},
                {"@U","w"},
                {"ai","j"},
                {"Oi","j"},
                {"au","w"},
                {"ou","w"},
                {"Ou","w"},
                {"@u","w"},
                {"3", "r"}
            };
        private readonly Dictionary<string, string> Delta5vvExceptions =
            new Dictionary<string, string>() {
                {"aI","I"},
                {"eI","I"},
                {"OI","I"},
                {"aU","U"},
                {"oU","U"},
                {"VI","I"},
                {"VU","U"},
                {"@U","U"},
                {"ai","i"},
                {"Oi","i"},
                {"au","u"},
                {"ou","u"},
                {"Ou","u"},
                {"@u","u"},
                {"3", "r"}
            };
        public string[] Prepare(string[] input, int tone) {
            string[] original = input.Select(p => Regex.Replace(p.ToLowerInvariant(), "[012]$", "")).ToArray();
            if (original == null) {
                return [];
            }
            List<string> finalProcessedPhonemes = new List<string>();

            for (int i = 0; i < original.Length; i++) {
                if (dictionaryReplacements.TryGetValue(original[i], out var replaced)) {
                    original[i] = replaced;
                }
            }

            // Splits diphthongs and affricates if not present in the bank
            string[] diphthongs = new[] { "aI", "eI", "OI", "aU", "oU", "VI", "VU", "@U", "ai", "ei", "Oi", "au", "ou", "Ou", "@u", };
            string[] affricates = new[] { "dZ", "tS" };

            foreach (string s in original) {
                if (diphthongs.Contains(s) && !HasOto($"- {s}", tone) && !HasOto(s, tone) && !HasOto(ValidateAlias($"- {s}"), tone) && !HasOto(ValidateAlias(s), tone)) {
                    finalProcessedPhonemes.AddRange(new string[] { s[0].ToString(), s[1] + '^'.ToString() });
                } else if (affricates.Contains(s) && !HasOto($"{s}A", tone) && !HasOto($"{s} A", tone) && !HasOto($"{s}Q", tone) && !HasOto($"{s} Q", tone)) {
                    finalProcessedPhonemes.AddRange(new string[] { s[0].ToString(), s[1].ToString() });
                } else {
                    finalProcessedPhonemes.Add(s);
                }
            }
            return finalProcessedPhonemes.ToArray();
        }
        public List<string> ProcessSyllable(Syllable syllable) {
            syllable.prevV = tails.Contains(syllable.prevV) ? "" : syllable.prevV;
            var replacedPrevV = ReplacePhoneme(syllable.prevV, syllable.tone);
            var prevV = string.IsNullOrEmpty(replacedPrevV) ? "" : replacedPrevV;
            string[] cc = syllable.cc.Select(c => ReplacePhoneme(c, syllable.tone)).ToArray();
            string v = ReplacePhoneme(syllable.v, syllable.vowelTone);
            List<string> vowels = new List<string> { v };
            string? basePhoneme;
            var phonemes = new List<string>();
            var lastC = cc.Length - 1;
            var firstC = 0;
            string[] CurrentWordCc = syllable.CurrentWordCc.Select(c => ReplacePhoneme(c, syllable.tone)).ToArray();
            string[] PreviousWordCc = syllable.PreviousWordCc.Select(c => ReplacePhoneme(c, syllable.tone)).ToArray();
            int prevWordConsonantsCount = syllable.prevWordConsonantsCount;


            var rv = $"- {v}";

            // Switch between phonetic systems, depending on certain aliases in the bank
            if (HasOto($"- i:", syllable.tone) || HasOto($"i:", syllable.tone) || (!HasOto($"- 3", syllable.tone) && !HasOto($"3", syllable.tone))) {
                isVocaSampa = true;
            }

            if (!HasOto($"- VI", syllable.tone) || HasOto($"VI", syllable.tone) || (!HasOto($"- VU", syllable.tone) && !HasOto($"VU", syllable.tone))) {
                isMissingCanadianRaising = true;
            }

            if (!HasOto($"- V", syllable.vowelTone) && !HasOto($"V", syllable.vowelTone)) {
                isSimpleDelta = true;
            }

            if (!HasOto($"- bV", syllable.vowelTone) && !HasOto($"bV", syllable.vowelTone)) {
                isTetoException = true;
            }

            if ((!HasOto($"- I", syllable.vowelTone) && !HasOto($"I", syllable.vowelTone)) || (!HasOto($"- U", syllable.vowelTone) && !HasOto($"U", syllable.vowelTone))) {
                isMiniDelta = true;
            }

            if (HasOto("あ", syllable.vowelTone) || HasOto("- あ", syllable.vowelTone)) {
                isEnPlusJa = true;
            }

            if (HasOto($"{prevV} r\\", syllable.tone)) {
                isTrueXSampa = true;
            }

            if (!HasOto($"- 3", syllable.tone) && !HasOto($"3", syllable.tone) && !HasOto($"- @`", syllable.tone) && !HasOto($"@`", syllable.tone)) {
                isSalemList = true;
            }

            if ((!HasOto($"N g", syllable.tone) || !HasOto($"N g-", syllable.tone)) && (!HasOto($"N k", syllable.tone) || !HasOto($"N k-", syllable.tone))) {
                isVelarNasalFallback = true;
            }

            if (HasOto("@5", syllable.vowelTone) || HasOto("u5", syllable.vowelTone) || HasOto("O5", syllable.vowelTone) || HasOto("A5", syllable.vowelTone) || HasOto("E5", syllable.vowelTone) || HasOto("I5", syllable.vowelTone) || HasOto("i5", syllable.vowelTone) || HasOto("- @5", syllable.vowelTone) || HasOto("- u5", syllable.vowelTone) || HasOto("- O5", syllable.vowelTone) || HasOto("- A5", syllable.vowelTone) || HasOto("- E5", syllable.vowelTone) || HasOto("- I5", syllable.vowelTone) || HasOto("- i5", syllable.vowelTone)) {
                isDarkLVowel = true;
            }

            if (syllable.IsStartingV) {
                if (HasOto(rv, syllable.vowelTone) || HasOto(ValidateAlias(rv), syllable.vowelTone)) {
                    basePhoneme = rv;
                } else {
                    basePhoneme = v;
                }
            } else if (syllable.IsVV) {
                if (!CanMakeAliasExtension(syllable)) {
                    if (HasOto($"{prevV} {v}", syllable.vowelTone) || HasOto(ValidateAlias($"{prevV} {v}"), syllable.vowelTone)) {
                        basePhoneme = $"{prevV} {v}";
                    } else if (HasOto($"{prevV}{v}", syllable.vowelTone) || HasOto(ValidateAlias($"{prevV}{v}"), syllable.vowelTone)) {
                        basePhoneme = $"{prevV}{v}";
                    }

                    // Diphthong Fallbacks
                    else if (diphthongSplits.ContainsKey(prevV) || diphthongTails.ContainsKey(prevV)) {
                        string cv = "";
                        if (diphthongSplits.ContainsKey(prevV)) {
                            var splitOverride = diphthongSplits[prevV];
                            var vc = splitOverride[0].Replace("{v}", v);
                            cv = splitOverride[1].Replace("{v}", v);
                            TryAddPhoneme(phonemes, syllable.tone, vc, ValidateAlias(vc));
                        }
                        else { // Default YAML diphthong logic
                            var tail = diphthongTails[prevV];
                            var vcSpace = $"{prevV} {tail}";
                            var vcNoSpace = $"{prevV}{tail}";
                            cv = $"{tail}{v}";
                            TryAddPhoneme(phonemes, syllable.tone, vcSpace, ValidateAlias(vcSpace), vcNoSpace, ValidateAlias(vcNoSpace));
                        }

                        if (HasOto(cv, syllable.vowelTone) || HasOto(ValidateAlias(cv), syllable.vowelTone)) {
                            basePhoneme = cv;
                        } else if  (!HasOto(cv, syllable.vowelTone) || !HasOto(ValidateAlias(cv), syllable.vowelTone)) {
                            basePhoneme = $"{diphthongTails[prevV]} {v}";
                        } else if (HasOto(v, syllable.vowelTone) || HasOto(ValidateAlias(v), syllable.vowelTone)) {
                            basePhoneme = v;
                        } else {
                            basePhoneme = ValidateAlias($"- {v}");
                            phonemes.Add($"{prevV} -");
                        }
                    } else {
                        if (HasOto(v, syllable.vowelTone) || HasOto(ValidateAlias(v), syllable.vowelTone)) {
                            basePhoneme = v;
                        } else {
                            basePhoneme = ValidateAlias($"- {v}");
                            phonemes.Add($"{prevV} -");
                        }
                    }
                }
                else {
                    basePhoneme = null;
                }
            } else if (syllable.IsStartingCVWithOneConsonant) {
                // TODO: move to config -CV or -C CV
                var rcv = $"- {cc[0]}{v}";
                var crv = $"{cc[0]} {v}";
                var cv = $"{cc[0]}{v}";
                if (HasOto(rcv, syllable.vowelTone) || HasOto(ValidateAlias(rcv), syllable.vowelTone)) {
                    basePhoneme = rcv;
                } else if ((!HasOto(rcv, syllable.vowelTone) && !HasOto(ValidateAlias(rcv), syllable.vowelTone)) && (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone))) {
                    basePhoneme = crv;
                    TryAddPhoneme(phonemes, syllable.tone, $"- {cc[0]}", ValidateAlias($"- {cc[0]}"));
                } else {
                    basePhoneme = cv;
                    TryAddPhoneme(phonemes, syllable.tone, $"- {cc[0]}", ValidateAlias($"- {cc[0]}"));
                }
            } else if (syllable.IsStartingCVWithMoreThanOneConsonant) {
                // try RCCV
                var rccv = $"- {string.Join("", cc)}{v}";
                var crv = $"{cc.Last()} {v}";
                var ucv = $"_{cc.Last()}{v}";
                if (HasOto(rccv, syllable.vowelTone) || HasOto(ValidateAlias(rccv), syllable.vowelTone)) {
                    basePhoneme = rccv;
                    lastC = 0;
                } else {
                    if (HasOto(ucv, syllable.vowelTone) || HasOto(ValidateAlias(ucv), syllable.vowelTone)) {
                        basePhoneme = ucv;
                    } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone)) {
                        basePhoneme = crv;
                    } else {
                        basePhoneme = $"{cc.Last()}{v}";
                    }
                    // try RCC
                    for (var i = cc.Length; i > 1; i--) {
                        if (TryAddPhoneme(phonemes, syllable.tone, $"- {string.Join("", cc.Take(i))}", ValidateAlias($"- {string.Join("", cc.Take(i))}"))) {
                            firstC = i - 1;
                            break;
                        }
                    }
                    if (phonemes.Count == 0) {
                        TryAddPhoneme(phonemes, syllable.tone, $"- {cc[0]}", ValidateAlias($"- {cc[0]}"));
                    }
                    // try CCV
                    for (var i = firstC; i < cc.Length - 1; i++) {
                        var ccv = string.Join("", cc.Skip(i)) + v;
                        if (HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone)) {
                            basePhoneme = ccv;
                            lastC = i;
                            break;
                        } else {
                            if (HasOto(ucv, syllable.vowelTone) || HasOto(ValidateAlias(ucv), syllable.vowelTone)) {
                                basePhoneme = ucv;
                                break;
                            } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone)) {
                                basePhoneme = crv;
                                break;
                            } else {
                                basePhoneme = $"{cc.Last()}{v}";
                                break;
                            }
                        }
                    }
                }
            } else { // VCV
                var vcv = $"{prevV} {cc[0]}{v}";
                var vcvEnd = $"{prevV}{cc[0]} {v}";
                var vccv = $"{prevV} {string.Join("", cc)}{v}";
                var crv = $"{cc.Last()} {v}";
                // Use regular VCV if the current word starts with one consonant and the previous word ends with none
                if (syllable.IsVCVWithOneConsonant && (HasOto(vcv, syllable.vowelTone) || HasOto(ValidateAlias(vcv), syllable.vowelTone)) && prevWordConsonantsCount == 0 && CurrentWordCc.Length == 1) {
                    basePhoneme = vcv;
                    // Use end VCV if current word does not start with a consonant but the previous word does end with one
                } else if (syllable.IsVCVWithOneConsonant && prevWordConsonantsCount == 1 && CurrentWordCc.Length == 0 && (HasOto(vcvEnd, syllable.vowelTone) || HasOto(ValidateAlias(vcvEnd), syllable.vowelTone))) {
                    basePhoneme = vcvEnd;
                    // Use regular VCV if end VCV does not exist
                } else if (syllable.IsVCVWithOneConsonant && !HasOto(vcvEnd, syllable.vowelTone) && !HasOto(ValidateAlias(vcvEnd), syllable.vowelTone) && (HasOto(vcv, syllable.vowelTone) || HasOto(ValidateAlias(vcv), syllable.vowelTone))) {
                    basePhoneme = vcv;
                    // VCV with multiple consonants, only for current word onset and null previous word ending
                    // TODO: multi-VCV for words ending with one or more consonants?
                } else if (syllable.IsVCVWithMoreThanOneConsonant && (HasOto(vccv, syllable.vowelTone) || HasOto(ValidateAlias(vccv), syllable.vowelTone)) && prevWordConsonantsCount == 0) {
                    basePhoneme = vccv;
                    lastC = 0;
                } else {
                    var cv = cc.Last() + v;
                    basePhoneme = cv;
                    if ((!HasOto(cv, syllable.vowelTone) && !HasOto(ValidateAlias(cv), syllable.vowelTone)) && (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone))) {
                        basePhoneme = crv;
                    }
                    // try CCV
                    if ((cc.Length - firstC > 1) && CurrentWordCc.Length >= 2) {
                        for (var i = firstC; i < cc.Length; i++) {
                            var ccv = $"{string.Join("", cc.Skip(0))}{v}";
                            var rccv = $"- {string.Join("", cc.Skip(0))}{v}";
                            var ucv = $"_{cc.Last()}{v}";
                            if (HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone)) {
                                lastC = 0;
                                basePhoneme = ccv;
                                break;
                            } else if (!HasOto(ccv, syllable.vowelTone) && !HasOto(ValidateAlias(ccv), syllable.vowelTone) && (HasOto(rccv, syllable.vowelTone) || HasOto(ValidateAlias(rccv), syllable.vowelTone))) {
                                lastC = 0;
                                basePhoneme = rccv;
                                break;
                            } else if ((!HasOto(rccv, syllable.vowelTone) || !HasOto(ValidateAlias(rccv), syllable.vowelTone)) && (HasOto(ucv, syllable.vowelTone) || HasOto(ValidateAlias(ucv), syllable.vowelTone))) {
                                basePhoneme = ucv;
                                break;
                            }
                        }
                    }
                    // try vcc
                    for (var i = lastC + 1; i >= 0; i--) {
                        var vr = $"{prevV} -";
                        var vcc = $"{prevV} {string.Join("", cc.Take(2))}";
                        var vcc2 = $"{prevV}{string.Join(" ", cc.Take(2))}";
                        var vc = $"{prevV} {cc[0]}";
                        if (i == 0) {
                            if (HasOto(vr, syllable.tone) || HasOto(ValidateAlias(vr), syllable.tone)) {
                                phonemes.Add(vr);
                            }
                        } else if ((HasOto(vcc, syllable.tone) || HasOto(ValidateAlias(vcc), syllable.tone)) && !affricate.Contains(string.Join("", cc.Take(2)))) {
                            phonemes.Add(vcc);
                            firstC = 1;
                            break;
                        } else if (HasOto(vcc2, syllable.tone) || HasOto(ValidateAlias(vcc2), syllable.tone)) {
                            phonemes.Add(vcc2);
                            firstC = 1;
                            break;
                        } else if (HasOto(vc, syllable.tone) || HasOto(ValidateAlias(vc), syllable.tone)) {
                            phonemes.Add(vc);
                            break;
                        } else {
                            continue;
                        }
                    }
                }
            }

            for (var i = firstC; i < lastC; i++) {
                // we could use some CCV, so lastC is used
                // we could use -CC so firstC is used
                var cc1 = $"{cc[i]} {cc[i + 1]}";
                var ccv = string.Join("", cc.Skip(i + 1)) + v;
                var rccv = $"- {string.Join("", cc.Skip(i + 1)) + v}";
                var ucv = $"_{cc.Last()}{v}";
                var crv = $"{cc.Last()} {v}";
                var cv = $"{cc.Last()}{v}";
                // Use [C1C2...] when current word starts with 2 consonants or more
                if (!HasOto(cc1, syllable.tone)) {
                    cc1 = ValidateAlias(cc1);
                }
                if (CurrentWordCc.Length >= 2 && !PreviousWordCc.Contains(cc1)) {
                    cc1 = $"{string.Join("", cc.Skip(i))}";
                }
                if (CurrentWordCc.Length >= 2) {
                    if (liquid.Contains(cc.Last()) || semivowel.Contains(cc.Last())
                        || liquid.Contains(ValidateAlias(cc.Last())) || semivowel.Contains(ValidateAlias(cc.Last()))) {
                        glides(cc1);
                    }
                }
                if (!HasOto(cc1, syllable.tone)) {
                    cc1 = ValidateAlias(cc1);
                }
                // Use [C1C2] when current word has 2 consonants or more and [C1C2C3...] does not exist
                if (!HasOto(cc1, syllable.tone) && CurrentWordCc.Length >= 2 && CurrentWordCc.Contains(cc1)) {
                    cc1 = $"{cc[i]}{cc[i + 1]}";
                }
                if (!HasOto(cc1, syllable.tone)) {
                    cc1 = ValidateAlias(cc1);
                }
                // Use [C1 C2] when either [C1C2] does not exist, or current word has 1 consonant or less and previous word has 1 consonant or more
                if ((!HasOto(cc1, syllable.tone)) || PreviousWordCc.Contains(cc1)) {
                    cc1 = $"{cc[i]} {cc[i + 1]}";
                }
                if (!HasOto(cc1, syllable.tone)) {
                    cc1 = ValidateAlias(cc1);
                }
                // Use UCV if it exists
                if ((HasOto(ucv, syllable.vowelTone) || HasOto(ValidateAlias(ucv), syllable.vowelTone)) && !cc1.Contains($"{cc[i]} {cc[i + 1]}")) {
                    basePhoneme = ucv;
                }
                if (i + 1 < lastC) {
                    var cc2 = $"{cc[i + 1]} {cc[i + 2]}";
                    if (!HasOto(cc2, syllable.tone)) {
                        cc2 = ValidateAlias(cc2);
                    }
                    // Use [C2C3...] when current word starts with 2 consonants or more
                    if (CurrentWordCc.Length >= 2 && !PreviousWordCc.Contains(cc2)) {
                        cc2 = $"{string.Join("", cc.Skip(i))}";
                    }
                    if (!HasOto(cc2, syllable.tone)) {
                        cc2 = ValidateAlias(cc2);
                    }
                    if (CurrentWordCc.Length >= 2) {
                        if (liquid.Contains(cc[i + 1]) || semivowel.Contains(cc[i + 1])
                            || liquid.Contains(ValidateAlias(cc[i + 1])) || semivowel.Contains(ValidateAlias(cc[i + 1]))) {
                            glides(cc1);
                        }
                    }
                    // Use [C2C3] when current word has 2 consonants or more and [C2C3C4...] does not exist
                    if (!HasOto(cc2, syllable.tone) && CurrentWordCc.Length >= 2 && CurrentWordCc.Contains(cc2)) {
                        cc2 = $"{cc[i + 1]}{cc[i + 2]}";
                    }
                    if (!HasOto(cc2, syllable.tone)) {
                        cc2 = ValidateAlias(cc2);
                    }
                    // Use [C2 C3] when either [C2C3] does not exist, or current word has 1 consonant or less and previous word has 2 consonants or more
                    if ((!HasOto(cc2, syllable.tone)) || PreviousWordCc.Contains(cc2)) {
                        cc2 = $"{cc[i + 1]} {cc[i + 2]}";
                    }
                    if (!HasOto(cc2, syllable.tone)) {
                        cc2 = ValidateAlias(cc2);
                    }
                    //Use CCV if it exists
                    if ((HasOto(ccv, syllable.vowelTone) || HasOto(ValidateAlias(ccv), syllable.vowelTone)) && CurrentWordCc.Length >= 2 && !PreviousWordCc.Contains(string.Join("", cc.Skip(i + 1)))) {
                        lastC = i;
                        basePhoneme = ccv;
                        // Use RCCV if it exists
                    } else if ((HasOto(rccv, syllable.vowelTone) || HasOto(ValidateAlias(rccv), syllable.vowelTone)) && CurrentWordCc.Length >= 2 && !PreviousWordCc.Contains(string.Join("", cc.Skip(i + 1)))) {
                        lastC = i;
                        basePhoneme = rccv;
                        // Use _CV if it exists
                    } else if ((HasOto(ucv, syllable.vowelTone) || HasOto(ValidateAlias(ucv), syllable.vowelTone)) && HasOto(cc2, syllable.vowelTone) && !cc2.Contains($"{cc[i + 1]} {cc[i + 2]}") && CurrentWordCc.Length >= 2) {
                        basePhoneme = ucv;
                        // Use spaced CV if it exists
                    } else if (HasOto(crv, syllable.vowelTone) || HasOto(ValidateAlias(crv), syllable.vowelTone)) {
                        basePhoneme = crv;
                        // Use normal CV
                    } else {
                        basePhoneme = cv;
                    }
                    if (HasOto(cc1, syllable.tone) && HasOto(cc2, syllable.tone) && !cc1.Contains($"{string.Join("", cc.Skip(i))}")) {
                        // like [V C1] [C1 C2] [C2 C3] [C3 ..]
                        phonemes.Add(cc1);
                    } else if (TryAddPhoneme(phonemes, syllable.tone, cc1)) {
                        // like [V C1] [C1 C2] [C2 ..]
                        if (cc1.Contains($"{string.Join("", cc.Skip(i))}")) {
                            i++;
                        }
                    } else {
                        // singular cc
                        if ((PreviousWordCc.Contains(cc1) == CurrentWordCc.Contains(cc1)) && !affricate.Contains(cc1)) {
                            cc1 = ValidateAlias(cc1);
                        } else {
                            TryAddPhoneme(phonemes, syllable.tone, cc1, cc[i], ValidateAlias(cc[i]));
                        }
                    }
                } else {
                    // like [V C1] [C1 C2]  [C2 ..] or like [V C1] [C1 -] [C3 ..]
                    TryAddPhoneme(phonemes, syllable.tone, cc1, cc[i], ValidateAlias(cc[i]));
                }
            }

            if (basePhoneme != null) phonemes.Add(basePhoneme);
            return phonemes;
        }

        public List<string> ProcessEnding(Ending ending) {
            string[] cc = ending.cc.Select(c => ReplacePhoneme(c, ending.tone)).ToArray();
            string v = ReplacePhoneme(ending.prevV, ending.tone);
            string t = ending.HasTail ? ReplacePhoneme(ending.tail, ending.tone) : "-";
            var phonemes = new List<string>();
            var vr = $"{v} {t}";

            var lastC = cc.Length - 1;
            var firstC = 0;

            if (ending.IsEndingV) {
                TryAddPhoneme(phonemes, ending.tone, vr, ValidateAlias(vr));
            } else if (ending.IsEndingVCWithOneConsonant) {
                var vc = $"{v} {cc[0]}";
                var vcr = $"{v} {cc[0]}{t}";
                var vcr2 = $"{v}{cc[0]} {t}";
                if (HasOto(vcr, ending.tone) || HasOto(ValidateAlias(vcr), ending.tone)) {
                    phonemes.Add(vcr);
                } else if ((!HasOto(vcr, ending.tone) && !HasOto(ValidateAlias(vcr), ending.tone)) && (HasOto(vcr2, ending.tone) || HasOto(ValidateAlias(vcr2), ending.tone))) {
                    phonemes.Add(vcr2);
                } else {
                    phonemes.Add(vc);
                    if (affricate.Contains(cc[0])) {
                        TryAddPhoneme(phonemes, ending.tone, $"{cc[0]} {t}", cc[0]);
                    } else {
                        TryAddPhoneme(phonemes, ending.tone, $"{cc[0]} {t}", ValidateAlias($"{cc[0]} {t}"));
                    }
                }
            } else {
                for (var i = lastC; i >= 0; i--) {
                    var vcc = $"{v} {string.Join("", cc.Take(2))}{t}";
                    var vcc2 = $"{v}{string.Join(" ", cc.Take(2))}{t}";
                    var vcc3 = $"{v}{string.Join(" ", cc.Take(2))}";
                    var vcc4 = $"{v} {string.Join("", cc.Take(2))}";
                    var vc = $"{v} {cc[0]}";
                    if (i == 0) {
                        if (HasOto(vr, ending.tone) || HasOto(ValidateAlias(vr), ending.tone)) {
                            phonemes.Add(vr);
                        }
                    } else if ((HasOto(vcc, ending.tone) || HasOto(ValidateAlias(vcc), ending.tone)) && lastC == 1) {
                        phonemes.Add(vcc);
                        firstC = 1;
                        break;
                    } else if ((HasOto(vcc2, ending.tone) || HasOto(ValidateAlias(vcc2), ending.tone)) && lastC == 1) {
                        phonemes.Add(vcc2);
                        firstC = 1;
                        break;
                    } else if (HasOto(vcc3, ending.tone) || HasOto(ValidateAlias(vcc3), ending.tone)) {
                        phonemes.Add(vcc3);
                        if (vcc3.EndsWith(cc.Last()) && lastC == 1) {
                            if (affricate.Contains(cc.Last())) {
                                TryAddPhoneme(phonemes, ending.tone, $"{cc.Last()} {t}", ValidateAlias($"{cc.Last()} {t}"), cc.Last(), ValidateAlias(cc.Last()));
                            } else {
                                TryAddPhoneme(phonemes, ending.tone, $"{cc.Last()} {t}", ValidateAlias($"{cc.Last()} {t}"));
                            }
                        }
                        firstC = 1;
                        break;
                    } else if (HasOto(vcc4, ending.tone) || HasOto(ValidateAlias(vcc4), ending.tone)) {
                        phonemes.Add(vcc4);
                        if (vcc4.EndsWith(cc.Last()) && lastC == 1) {
                            if (affricate.Contains(cc.Last())) {
                                TryAddPhoneme(phonemes, ending.tone, $"{cc.Last()} {t}", ValidateAlias($"{cc.Last()} {t}"), cc.Last(), ValidateAlias(cc.Last()));
                            } else {
                                TryAddPhoneme(phonemes, ending.tone, $"{cc.Last()} {t}", ValidateAlias($"{cc.Last()} {t}"));
                            }
                        }
                        firstC = 1;
                        break;
                    } else {
                        phonemes.Add(vc);
                        break;
                    }
                }


                for (var i = firstC; i < lastC; i++) {
                    // all CCs except the first one are /C1C2/, the last one is /C1 C2-/
                    // but if there is no /C1C2/, we try /C1 C2-/, vise versa for the last one
                    var cc1 = $"{cc[i]} {cc[i + 1]}";
                    if (i < cc.Length - 2) {
                        var cc2 = $"{cc[i + 1]} {cc[i + 2]}";
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = ValidateAlias(cc1);
                        }
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = $"{cc[i]}{cc[i + 1]}";
                        }
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = ValidateAlias(cc1);
                        }
                        if (!HasOto(cc2, ending.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (!HasOto(cc2, ending.tone)) {
                            cc2 = $"{cc[i + 1]}{cc[i + 2]}";
                        }
                        if (!HasOto(cc2, ending.tone)) {
                            cc2 = ValidateAlias(cc2);
                        }
                        if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i]} {cc[i + 1]}{cc[i + 2]}{t}", ValidateAlias($"{cc[i]} {cc[i + 1]}{cc[i + 2]}{t}"))) {
                            // like [C1 C2-][C3 ...]
                            i++;
                        } else if (HasOto(cc1, ending.tone) && (HasOto(cc2, ending.tone) || HasOto($"{cc[i + 1]} {cc[i + 2]}{t}", ending.tone) || HasOto(ValidateAlias($"{cc[i + 1]} {cc[i + 2]}{t}"), ending.tone))) {
                            // like [C1 C2][C2 ...]
                            phonemes.Add(cc1);
                        } else if ((HasOto(cc[i], ending.tone) || HasOto(ValidateAlias(cc[i]), ending.tone) && (HasOto(cc2, ending.tone) || HasOto($"{cc[i + 1]} {cc[i + 2]}{t}", ending.tone) || HasOto(ValidateAlias($"{cc[i + 1]} {cc[i + 2]}{t}"), ending.tone)))) {
                            // like [C1 C2-][C3 ...]
                            phonemes.Add(cc[i]);
                        } else if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} {cc[i + 2]}{t}", ValidateAlias($"{cc[i + 1]} {cc[i + 2]}{t}"))) {
                            // like [C1 C2-][C3 ...]
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]}{cc[i + 2]}", ValidateAlias($"{cc[i + 1]}{cc[i + 2]}"))) {
                            // like [C1C2][C2 ...]
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, cc1, ValidateAlias(cc1))) {
                            i++;
                        } else {
                            // like [C1][C2 ...]
                            TryAddPhoneme(phonemes, ending.tone, cc[i], ValidateAlias(cc[i]), $"{cc[i]} {t}", ValidateAlias($"{cc[i]} {t}"));
                            TryAddPhoneme(phonemes, ending.tone, cc[i + 1], ValidateAlias(cc[i + 1]), $"{cc[i + 1]} {t}", ValidateAlias($"{cc[i + 1]} {t}"));
                            i++;
                        }
                    } else {
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = ValidateAlias(cc1);
                        }
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = $"{cc[i]}{cc[i + 1]}";
                        }
                        if (!HasOto(cc1, ending.tone)) {
                            cc1 = ValidateAlias(cc1);
                        }
                        if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i]} {cc[i + 1]}{t}", ValidateAlias($"{cc[i]} {cc[i + 1]}{t}"))) {
                            // like [C1 C2-]
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, cc1, ValidateAlias(cc1))) {
                            // like [C1 C2][C2 -]
                            TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} {t}", ValidateAlias($"{cc[i + 1]} {t}"), cc[i + 1], ValidateAlias(cc[i + 1]));
                            i++;
                        } else if (TryAddPhoneme(phonemes, ending.tone, $"{cc[i]}{cc[i + 1]}", ValidateAlias($"{cc[i]}{cc[i + 1]}"))) {
                            // like [C1C2][C2 -]
                            TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} {t}", ValidateAlias($"{cc[i + 1]} {t}"), cc[i + 1], ValidateAlias(cc[i + 1]));
                            i++;
                        } else {
                            // like [C1][C2 -]
                            TryAddPhoneme(phonemes, ending.tone, cc[i], ValidateAlias(cc[i]), $"{cc[i]} {t}", ValidateAlias($"{cc[i]} {t}"));
                            TryAddPhoneme(phonemes, ending.tone, $"{cc[i + 1]} {t}", ValidateAlias($"{cc[i + 1]} {t}"), cc[i + 1], ValidateAlias(cc[i + 1]));
                            i++;
                        }
                    }
                }
            }
            return phonemes;
        }

        internal string ValidateAlias(string alias, int tone = 0) {
            if (string.IsNullOrEmpty(alias)) {
                return alias;
            }

            if (HasOto(alias, tone)) {
                return alias;
            }

            string baseResolved = alias;
            if (!string.IsNullOrEmpty(baseResolved) && HasOto(baseResolved, tone)) {
                return baseResolved;
            }
            if (!string.IsNullOrEmpty(baseResolved)) {
                alias = baseResolved;
            }

            string candidate = alias;

            if (isVocaSampa) {
                foreach (var syllable in vocaSampa) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isSimpleDelta) {
                foreach (var syllable in simpleDelta) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isMiniDelta) {
                foreach (var syllable in miniDelta) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isEnPlusJa) {
                foreach (var syllable in enPlusJa) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isTrueXSampa) {
                foreach (var syllable in trueXSampa) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isSalemList) {
                foreach (var syllable in salemList) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isVelarNasalFallback) {
                foreach (var syllable in velarNasalFallback) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isTetoException) {
                foreach (var syllable in tetoException) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isMissingCanadianRaising) {
                foreach (var syllable in CanadianRaising) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            if (isDarkLVowel) {
                foreach (var syllable in darkLVowel) {
                    candidate = candidate.Replace(syllable.Key, syllable.Value);
                }
            }

            // Split diphthongs adjuster
            candidate = candidate.Replace("U^", "U")
                                .Replace("I^", "I")
                                .Replace("u^", "u")
                                .Replace("i^", "i");

            // Other validations
            if (!candidate.Contains("@r") && !candidate.Contains("3r")) {
                foreach (var consonant1 in new[] { "r ", "r\\ ", }) {
                    foreach (var consonant2 in consonants) {
                        candidate = candidate.Replace(consonant1 + consonant2, "3 " + consonant2);
                    }
                }
            }

            // Return the replacement if it exists in the OTO, otherwise keep original
            return HasOto(candidate, tone) ? candidate : alias;
        }


}
