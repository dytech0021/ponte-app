package com.dytech.ponte

import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioTrack
import android.os.Build
import ponte.core.Sound

/**
 * Toca o som que chega do computador. Antes de começar (e depois de cada falha) entra um instante de
 * silêncio, que dá folga para os pedaços que a rede atrasa. Quando a fila do aparelho enche, o que
 * chega é descartado: melhor perder um trecho que ir acumulando atraso.
 */
class SoundPlayer {
    private var track: AudioTrack? = null
    private var rate = 0
    private var channels = 0
    private var underruns = 0
    private var needsLead = true
    private var unavailable = false
    private var released = false

    /** Chamado pela thread que recebe as mensagens da sessão. */
    @Synchronized
    fun play(sound: Sound) {
        if (released || sound.samples.isEmpty()) return
        val current = trackFor(sound.rate, sound.channels) ?: return
        val now = current.underrunCount
        if (needsLead || now != underruns) {
            underruns = now
            needsLead = false
            val threshold = if (Build.VERSION.SDK_INT >= 31) current.startThresholdInFrames else current.bufferSizeInFrames
            val lead = ShortArray(maxOf(0, threshold * sound.channels - sound.samples.size))
            current.write(lead, 0, lead.size, AudioTrack.WRITE_NON_BLOCKING)
        }
        current.write(sound.samples, 0, sound.samples.size, AudioTrack.WRITE_NON_BLOCKING)
    }

    private fun trackFor(wantedRate: Int, wantedChannels: Int): AudioTrack? {
        track?.let { if (wantedRate == rate && wantedChannels == channels) return it }
        closeTrack()
        // Um aparelho sem saída para esse formato não passa a ter no pedaço seguinte.
        if (unavailable) return null
        val mask = if (wantedChannels == 1) AudioFormat.CHANNEL_OUT_MONO else AudioFormat.CHANNEL_OUT_STEREO
        val minimum = AudioTrack.getMinBufferSize(wantedRate, mask, AudioFormat.ENCODING_PCM_16BIT)
        val created = try {
            if (minimum <= 0) null else AudioTrack.Builder()
                .setAudioAttributes(AudioAttributes.Builder().setUsage(AudioAttributes.USAGE_MEDIA).setContentType(AudioAttributes.CONTENT_TYPE_MUSIC).build())
                .setAudioFormat(AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(wantedRate).setChannelMask(mask).build())
                .setBufferSizeInBytes(maxOf(minimum, wantedRate * wantedChannels * 2 * BUFFER_MS / 1000))
                .setTransferMode(AudioTrack.MODE_STREAM)
                .build()
                .also {
                    // Sons curtos também precisam ultrapassar o limiar inicial, inclusive antes do Android 12.
                    val startFrames = wantedRate * LEAD_MS / 1000
                    if (Build.VERSION.SDK_INT >= 31) it.setStartThresholdInFrames(startFrames)
                    else it.setBufferSizeInFrames(startFrames)
                    it.play()
                }
        } catch (e: RuntimeException) {
            null
        }
        if (created == null) {
            unavailable = true
            return null
        }
        track = created
        rate = wantedRate
        channels = wantedChannels
        underruns = created.underrunCount
        needsLead = true
        return created
    }

    private fun closeTrack() {
        val old = track ?: return
        track = null
        try {
            old.pause()
            old.flush()
        } catch (e: IllegalStateException) {
        }
        old.release()
    }

    /** Para de tocar na hora, sem encerrar: o próximo trecho recomeça com a folga inicial. */
    @Synchronized
    fun silence() {
        closeTrack()
    }

    @Synchronized
    fun release() {
        released = true
        closeTrack()
    }

    private companion object {
        const val LEAD_MS = 150
        const val BUFFER_MS = 600
    }
}
