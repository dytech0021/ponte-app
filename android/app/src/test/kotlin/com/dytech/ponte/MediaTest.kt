package com.dytech.ponte

import android.app.Activity
import android.graphics.Bitmap
import android.graphics.Color
import android.media.AudioTrack
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import org.robolectric.annotation.Implementation
import org.robolectric.annotation.Implements
import org.robolectric.shadows.ShadowAudioTrack
import ponte.core.Sound

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], shadows = [StartupAudioTrack::class])
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class MediaTest {
    @Test
    fun notificacaoCurtaIniciaSemEsperarOutroSom() {
        val player = SoundPlayer()
        try {
            player.play(Sound(ShortArray(1920) { 700 }, 48000, 2))
            assertTrue("um trecho curto deve atingir o limiar de reprodução", StartupAudioTrack.started)
            player.silence()
            player.play(Sound(ShortArray(1920) { 900 }, 48000, 2))
            assertTrue("depois de silenciar também deve recomeçar", StartupAudioTrack.started)
        } finally { player.release() }
    }

    @Test
    @Config(sdk = [28], shadows = [StartupAudioTrack::class])
    fun notificacaoCurtaTambemIniciaNoAndroidAntigo() = notificacaoCurtaIniciaSemEsperarOutroSom()

    @Test
    fun ponteiroOcultoNaoViraSetaMasVersaoAntigaTemSeta() {
        val controller = Robolectric.buildActivity(Activity::class.java).setup()
        try {
            val view = RemoteView(controller.get())
            controller.get().setContentView(view)
            view.layout(0, 0, 320, 180)
            val background = Color.rgb(20, 120, 200)
            view.applyFrame(Apoio.quadro(320, 180, background))
            Apoio.avanca(20)
            val baseline = Apoio.desenha(view)
            view.touchpad = true
            fun changes(image: Bitmap): Int {
                var count = 0
                for (y in 0 until image.height) for (x in 0 until image.width)
                    if (image.getPixel(x, y) != baseline.getPixel(x, y)) count++
                image.recycle()
                return count
            }
            assertTrue("sem mensagem de cursor mantém a seta de compatibilidade", changes(Apoio.desenha(view)) > 0)
            view.setPointer(null)
            Apoio.avanca(20)
            assertEquals("cursor explicitamente oculto não deve aparecer", 0, changes(Apoio.desenha(view)))
            baseline.recycle()
        } finally { controller.pause().stop().destroy() }
    }
}

/** Modelo do limiar documentado do AudioTrack: não toca até a primeira escrita atingir esse limite. */
@Implements(AudioTrack::class)
class StartupAudioTrack : ShadowAudioTrack() {
    private var size = 28800
    private var threshold = size
    private var written = 0
    @Implementation fun getBufferSizeInFrames() = size
    @Implementation fun setBufferSizeInFrames(value: Int): Int { size = value; return size }
    @Implementation(minSdk = 31) fun getStartThresholdInFrames() = threshold
    @Implementation(minSdk = 31) fun setStartThresholdInFrames(value: Int): Int { threshold = value; return threshold }
    @Implementation fun getUnderrunCount() = 0
    @Implementation fun write(data: ShortArray, offset: Int, count: Int, mode: Int): Int {
        written += count / 2
        started = written >= minOf(size, threshold)
        return count
    }
    @Implementation override fun flush() { written = 0; started = false }
    companion object { var started = false }
}
