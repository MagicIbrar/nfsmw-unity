# Movies (FMV): EA VP6 container, codecs, when the game plays what

Sources: decomp `refs/nfsmw/src/Speed/Indep/Src/Frontend/` (MoviePlayer/MoviePlayer.cpp, FEObjectCallbacks.cpp,
MenuScreens/Common/FEAnyMovieScreen.cpp + FEAnyTutorialScreen.cpp, MenuScreens/InGame/InGameMovieScreen.cpp +
InGameTutorialScreen.cpp, MenuScreens/Loading/FEMovieScreen.cpp, SubTitle.cpp), speed.exe v1.3 (addresses below are VAs,
"file" = file offset), the full install's `MOVIES\` (29 files), `SUBTITLES\`, and the FEng packages. Port: `Core/EaMovie.cs`,
`Core/Vp6Decoder.cs`, `Core/Vp6Tables.cs`, `Runtime/Game/Media.cs` (MoviePlayer, MovieAudioFeed), `Shaders/MovieYUV.shader`,
harness `NFSMWStaging/Harness/MovieProbe.cs` (`movie` mode).

## 1. Files

`MOVIES\<name>_<language>_<ntsc|pal>.vp6`. CalculateMovieFilename (FEObjectCallbacks.cpp:37; format string
`%sMOVIES\%s%s%s%s` at file 0x49B39C, `.vp6` 0x49B3B0, `_ntsc` 0x49B3C4): prefix "", language = `_` + GetLanguageName,
`_ntsc` unless BuildRegion::IsPal. FEngMovieStarter::Callback: try the current language, then English; if the file does
not exist the movie-finished message (0xC3960EB9) is queued at once (the screen closes as if the movie had ended).

The full install has (all `_english_ntsc`): blacklist_01..15, bounty/drag/pursuit/speedtrap/tollbooth_tutorial,
storyfmv_bla134, bus12, cro06_coh06a, her136, pin11, rac01, rap30, raz08, saf25. Not shipped (boot movies, cut ones):
ealogo, eahd_bumper, psa, intro_movie, attract_movie, sprint_tutorial, storyfmv_dda01/epi138/pol17_mot21/roc02.

## 2. Container (all little-endian unless noted)

Flat chunk stream `{char tag[4]; u32 size (includes the 8-byte header); payload}`:

| tag | payload |
|---|---|
| `MVhd` (0x20) | char[4] codec, stored "06PV" = `VP60`; u16 width 1024; u16 height 512; u32 frame count; u32 largest chunk; u32 rate 982047; u32 scale 32767 (fps = rate/scale = 29.97) |
| `SCHl` (0x28) | EA audio header, "GSTR" variant: `GSTR`, u32 1, then tag/len/BE-value fields: 00 (u32 id), 06 = 0x65 (priority), FD, 80 = 3 (version), 85 = samples, 82 = 2 (channels), FF. **No 0x84 rate field** |
| `MV0K` | VP6 key frame |
| `MV0F` | VP6 inter frame |
| `SCCl` (0x0C) | u32 **BE** number of SCDl blocks |
| `SCDl` | audio block: u32 BE samples, u32 BE offset per channel (from the end of this table), channel data |
| `SCEl` | end of audio |

Order: MVhd, SCHl, MV0K, SCCl, then MV0F/SCDl alternating (one audio block of ~1601 samples per frame), MV0K every
60-120 frames. In rac01, raz08 and bounty_tutorial the video runs 13-27 frames past the audio.

Audio rate: [?] 48000 Hz assumed when no 0x84 field (`EaMovieFile.GstrDefaultRate`). Evidence: samples / 48000 equals
the video length in all 29 files (e.g. blacklist_01: 1492080 / 48000 = 31.09 s, 932 frames / 29.97 = 31.10 s); FFmpeg
also reports 48000. The exe's RCMP default was not located.

## 3. Audio codec: EA-XA "R3"

Per channel: frames of 1 + 14 bytes = 28 samples. Byte 0: coefficient index (high nibble), shift (low nibble).
`s = clip16(((sext4(nibble) << (20 - shift)) + cur*c1 + prev*c2) >> 8)` with c1 = {0, 240, 460, 392},
c2 = {0, 0, -208, -220}; high nibble first. A frame starting 0xEE: two BE words (new cur, prev), then 28 BE PCM samples
(the history is *not* updated from those samples). History carries across blocks. This is FFmpeg's adpcm_ea_r3 (R2 with
big-endian block words). `EaMovieAudio` decodes it with integer arithmetic; Core/EaXa (float, sound banks) is untouched.
Verified: storyfmv_rac01's 2,380,800 stereo samples are bit-identical to FFmpeg's decode.

## 4. Video codec: On2 VP6 (EA "VP60")

speed.exe links EA's RCMP AV player with VP6_CODEC_INTERNAL (strings near file 0x4C27E8, tables at file
0x4C3800-0x4C5960). Every MW movie: key frame byte 1 = 0x38 → **sub-version 7, no filter header, progressive**;
1024x512 coded = displayed (64x32 macroblocks); coefficient partition separate (`separated` bit set); inter frames are
mostly **Huffman-coded** coefficients, key frames boolean-coded. The decoder implements the spec / FFmpeg vp56+vp6
algorithms in own C#:

* frame header: byte 0 = !key<<7 | quantiser<<1 | separated; key: byte 1 = sub_version<<3 | filter_header bits | interlaced,
  u16 BE coefficient-partition offset, rows, cols, display rows, display cols; inter: u16 offset; then the boolean coder
  (24-bit window, `low = 1 + ((high-1)*p >> 8)`), golden-frame flag, use_huffman.
* models: MB-type statistics (16 pre-defined vectors + deltas), vector models, DC/AC/run coefficient models, reorder
  bands; MB types via the previous type's probabilities; MV prediction from 12 neighbour positions; 4MV; golden frame.
* coefficients: boolean token tree or Huffman trees built from the same models (VP6 Huffman build: counts from the
  probability tree, sorted by count then higher symbol first, new nodes before equal counts), DC prediction from left/above
  blocks with the same reference frame, dequantisation (DC/AC tables), VP3 integer IDCT (DC-only add `(dc+15)>>5`).
* motion compensation: quarter-pel luma / eighth-pel chroma, bilinear (h264-chroma style, two-pass for diagonal), edges
  clamped. Pictures are **bottom-up** (FFmpeg treats codec VP6 as flipped); the decoder works in coded order and Unity's
  texture origin is bottom-left, so no flip is needed (RenderMovie also flips V for movie type 0).

Exe-specific details found while matching (see Vp6Tables.cs comments):
* the pre-defined MB-type statistic table (file 0x4C5548) is laid out **[ctx 3][16][type 10]{stat0, stat1}**
  (speed.exe 0x808D13 indexes `(idx + ctx*16) * 20`); the default table (file 0x4C5908) is stored {10 x stat1, 10 x stat0}.
* the delta update clamps the statistic to 0..255 (0x808D47); FFmpeg lets the byte wrap (never differs on these files).
* the delta tree's probabilities (171, 83, 199, 140, 125, 104) are immediates in 0x808C12, not a table.
* vector-model update probabilities are stored per component {dct, sig, pdv[7], fdv[8]} (file 0x4C54F8).

Not implemented ([?], no MW movie uses them): interlaced pictures, the advanced profile's 4-tap / deblocking filters
(filter header), alpha.

Verification (harness `movie md5` vs the user's installed ffmpeg 8.0.1 `-f framemd5`, reference output kept in scratch):
**every frame FFmpeg outputs is bit-identical for all 29 movies** (43,122 frames). In rac01 / raz08 / bounty_tutorial
FFmpeg stops at the end of the audio and drops the last 27 / 13 / 3 frames; the port decodes and shows them [?] (whether
RCMP shows them is unknown; UpdateFunction ends on "no frame and audio finished").

Speed (harness, .NET 8, one thread): 43,165 frames in 131 s = **3.0 ms/frame** (worst single frames 5-43 ms, GC/OS
jitter), i.e. ~10x real time. `movie stream`: the threaded EaMovieStream at 1x shows 914/914 frames of blacklist_15 in
30.5 s with no skipped frame. Unity's Mono JIT is slower [?] but decoding runs on its own thread with 8 frames buffered.

## 5. When the original plays which movie

| movie | screen | trigger |
|---|---|---|
| storyfmv_rac01 | InGameAnyMovie | 16.1.0 IntroMovie (EPlayRaceMovie in the basicrace state graph) |
| storyfmv_cro06_coh06a | InGameAnyMovie | 16.2.2 IntroMovie |
| storyfmv_raz08 | InGameAnyMovie | 16.2.3 IntroMovie |
| storyfmv_pin11 | InGameAnyMovie | 16.2.1 (D-Day race) IntroMovie |
| storyfmv_bus12 | InGameAnyMovie | 16.2.1 OutroMovie |
| storyfmv_saf25 | FEAnyMovie | FEAnyMovieScreen::PlaySafehouseIntroMovie (Game.DoSafeHouseIntro), returns to SafeHouseRivalBio.fng |
| blacklist_NN | FEAnyMovie | uiRepSheetRivalFlow stage 5 (`blacklist_%02d`, current bin) and the rival bio screen |
| storyfmv_rap30 | FEAnyMovie | rival flow stage 6, first time at bin 13 (career flag HasRapSheet) |
| storyfmv_bla134 | InGameAnyMovie | 1.5.2 IntroMovie |
| storyfmv_her136 | InGameAnyMovie | epic pursuit 1.8.1 IntroMovie |
| drag/speedtrap/tollbooth_tutorial | (In)GameAnyTutorial | race state `tutorial` (career, once per career) and the Challenge Series / icon menu |
| bounty/pursuit_tutorial | (In)GameAnyTutorial | rap sheet Bounty / Milestones screens |
| ealogo, eahd_bumper, psa, intro_movie, attract_movie | boot MovieScreen | boot flow (files absent in this install) |

Skipping (__PAD_ACCEPT__ / __PAD_START__):
* FEAnyMovieScreen: always, except during D-Day when MoviePlayer_Bypass (more than 5000 ms since MoviePlayer_Play).
* InGameAnyMovieScreen: during D-Day, or SkipMovies, or after 5 s (MoviePlayer_Bypass).
* Tutorial screens: dismiss on Accept/Start; on the first viewing per career (HasDoneXTutorial) the screen does not send
  the 0x59291F95 "skippable" message to the package [?] (the package's event handler, 'call' script 27.6 s, was not traced),
  so the port blocks skipping on a first viewing when the caller says so.
* Boot MovieScreen: ealogo / eahd_bumper not bypassable, psa / intro / attract bypassable.

Volume (MovieVolumeArray, MoviePlayer.cpp:61, strings at file 0x49A7A0, 0..127): storyfmv 0x50, blacklist 0x2D,
tutorials 0x37, ealogo 0x3C, attract 0x41, psa 0x32, others 0x7F. [?] mapped linearly (v/127) to the AudioSource.

Screen: the "Movie" FE_Movie object (0x0348FF9F) of FEAnyMovie / InGameAnyMovie / the tutorial packages is **640 x 512**
FE units at the centre (WS_ packages 900 x 512; PC's eIsWidescreen 0x4FB000 returns false). cFEngRender::RenderMovie
(0x599DB0) maps the whole texture onto it (UVs = size / next power of two = 1, V flipped). So the 1024x512 picture is
stretched to 4:3 proportions (anamorphic pixels; story movies have letterbox bars burnt in) and 16 units top and bottom
fall off the 480-unit screen. Behind it a black rectangle (C6AFDD7E, 1200 x 650). Fades: the screens post
EFadeScreenOff(FEHASH_15_IN) when they open (fade in [?] 0.5 s in the port), InGameAnyMovieScreen::DismissMovie posts
EFadeScreenOn (the next screen fades back in). Game sound is paused during movies (MovieScreen: ESndGameState /
SetSoundControlState(SNDSTATE_FMV); MoviePlayer::Update on finish: SoundPause(false, eSNDPAUSE_MOVIE)).

Subtitles (SubTitle.cpp): `SUBTITLES\<name>` = SubtitleInfo {u16 startTime in 1/10 s, u16 pad, u32 string hash},
ended by startTime 0xFFFF; entry 0 is a real line (the pad of entry 0 is 0x87 garbage). Hash 0x1A20BA clears the text.
Shown only when the language is not English **or** the movie is a tutorial. Tutorials show two lines, the strings
`bStringHash("_A", hash)` / `("_B", hash)` (e.g. drag: "Focus on hitting perfect shifts." / "The Heads up display...") at
FE (-230, 23) and (-228, 73) with arrows at x -243, plus the title `TUTORIAL_<X>_LABEL` at (-250, -168). Other movies:
one centred string at (0, 176) over a black 80/255 bar. Timing starts at the first picture (SubTitler::NotifyFirstFrame).

## 6. Port

* `EaMovieFile` (chunk reader), `EaMovieAudio` (EA-XA R3), `Vp6Decoder`, `EaMovieStream` (worker thread: decodes in file
  order into 8 pooled pictures and a 4 s audio ring; `Take(time)` returns the newest due frame).
* `MoviePlayer.TryPlay(name, done[, MovieScreen, firstTutorialView])` (old API kept): picks the file as §1, black camera at
  depth 200, three R8 textures + `NFSMW/MovieYUV` (BT.601 limited range [?]) drawn in OnGUI at the 640x512 FE rectangle,
  soundtrack through `MovieAudioFeed` (OnAudioFilterRead on a child AudioSource that ignores the listener pause; linear
  resampling if the output rate is not 48 kHz). The audio output position is the clock; without an audio device a
  stopwatch, and the soundtrack is dropped at the clock's pace. AudioListener.pause silences game sound and the music
  system during the movie. Skip rules as §5 (`MoviePlayer.IsDDay` defaults to the profile's prologue stage).
  `MoviePlayer.SkipAll()` ends the current movie; under `-qa` movies end after `QaSeconds` (4 s) [ours] so the QA tour's
  waits hold. A converted copy (name.mp4/.webm) still plays through VideoPlayer if the VP6 cannot be opened/decoded.

## 7. Open [?]

* RCMP's YUV→RGB matrix and whether the last video frames after the audio end are shown.
* The fade length of FEHASH_15_IN; the tutorial packages' text animation (scripts BCBF0306 / 549F7E87, 500 ms) and wrap
  width; the arrow image (ARROW_MAIN.tga) is drawn as a ">" glyph.
* The first-viewing tutorial lock (what 0x59291F95 does in the package).
* The audio default rate for GSTR headers (48000 by evidence).
