/*
 * Single C translation unit for MIT / Public-Domain vendor audio libraries:
 *   - stb_vorbis (Public Domain / MIT)
 *   - miniaudio  (Public Domain / MIT-0, includes dr_mp3, dr_wav, dr_flac)
 */

#ifdef _MSC_VER
#pragma warning(push, 0)
#endif

#include "torii_miniaudio_config.h"

#define STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#define MINIAUDIO_IMPLEMENTATION
#include "miniaudio.h"

#undef STB_VORBIS_HEADER_ONLY
#include "stb_vorbis.c"

#ifdef _MSC_VER
#pragma warning(pop)
#endif
