// Prints the nrsc5 struct offsets the C# plugin reads, to verify the hard-coded values in HdDecoder.cs.
#include <stddef.h>
#include <stdio.h>
#include <nrsc5.h>

#define P(expr, expected) printf("%-40s %3d  (plugin uses %3d) %s\n", #expr, (int)(expr), expected, (int)(expr) == expected ? "OK" : "MISMATCH")

int main(void)
{
    P(offsetof(nrsc5_event_t, sync.freq_offset), 8);
    P(offsetof(nrsc5_event_t, mer.lower), 8);
    P(offsetof(nrsc5_event_t, mer.upper), 12);
    P(offsetof(nrsc5_event_t, ber.cber), 8);
    P(offsetof(nrsc5_event_t, audio.program), 8);
    P(offsetof(nrsc5_event_t, audio.data), 16);
    P(offsetof(nrsc5_event_t, audio.count), 24);
    P(offsetof(nrsc5_event_t, audio.flags), 32);
    P(offsetof(nrsc5_event_t, id3.program), 8);
    P(offsetof(nrsc5_event_t, id3.title), 16);
    P(offsetof(nrsc5_event_t, id3.artist), 24);
    P(offsetof(nrsc5_event_t, id3.album), 32);
    P(offsetof(nrsc5_event_t, id3.xhdr.mime), 64);
    P(offsetof(nrsc5_event_t, id3.xhdr.lot), 72);
    P(offsetof(nrsc5_event_t, lot.lot), 12);
    P(offsetof(nrsc5_event_t, lot.size), 16);
    P(offsetof(nrsc5_event_t, lot.name), 24);
    P(offsetof(nrsc5_event_t, lot.data), 32);
    P(offsetof(nrsc5_event_t, lot.service), 48);
    P(offsetof(nrsc5_event_t, lot.component), 56);
    P(offsetof(nrsc5_event_t, audio_service.program), 8);
    P(offsetof(nrsc5_event_t, audio_service.type), 16);
    P(offsetof(nrsc5_event_t, station_name.name), 8);
    P(offsetof(nrsc5_event_t, station_slogan.slogan), 8);
    P(offsetof(nrsc5_event_t, station_message.message), 8);
    P(offsetof(nrsc5_event_t, emergency_alert.message), 8);
    P(offsetof(nrsc5_event_t, here_image.image_type), 8);
    P(offsetof(nrsc5_event_t, here_image.n1), 16);
    P(offsetof(nrsc5_event_t, here_image.name), 48);
    P(offsetof(nrsc5_event_t, here_image.size), 56);
    P(offsetof(nrsc5_event_t, here_image.data), 64);
    P(offsetof(nrsc5_sig_service_t, type), 8);
    P(offsetof(nrsc5_sig_service_t, number), 10);
    P(offsetof(nrsc5_sig_component_t, data.mime), 20);
    P(offsetof(nrsc5_sig_service_t, audio_component), 32);
    P(offsetof(nrsc5_sig_component_t, audio.port), 12);
    P(NRSC5_EVENT_EMERGENCY_ALERT, 22);
    P(NRSC5_EVENT_HERE_IMAGE, 23);
    P(NRSC5_EVENT_STATION_MESSAGE, 18);
    P(NRSC5_SIG_SERVICE_AUDIO, 0);
    return 0;
}
