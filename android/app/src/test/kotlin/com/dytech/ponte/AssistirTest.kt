package com.dytech.ponte

import android.content.Intent
import android.content.pm.PackageManager
import android.view.View
import android.webkit.WebView
import android.widget.EditText
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config

/** Assistir a uma transmissão de tela dentro do aplicativo, pelo link colado no campo de endereço. */
@RunWith(org.robolectric.RobolectricTestRunner::class)
@Config(sdk = [35])
class AssistirTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val link = "https://filabox3d.com.br/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl"

    @Before fun prepara() {
        context.getSharedPreferences("ponte", 0).edit().clear().commit()
        Store(context).relayServer = "filabox3d.com.br:7412"
        DeviceIdentity.replacement = Apoio.identidade()
        UpdateFlow.automatic = false
        MainActivity.presenceSource = { _, _, _ -> emptyMap() }
    }

    @After fun desfaz() {
        DeviceIdentity.replacement = null
        MainActivity.presenceSource = { server, key, targets -> ponte.core.Presence.check(server, key, targets) }
    }

    @Test fun linkDaTransmissaoAbreATelaDeAssistirEIdContinuaAbrindoSessao() {
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        val address = activity.findViewById<EditText>(R.id.endereco)
        fun connect(text: String): Intent? {
            address.setText(text)
            activity.findViewById<View>(R.id.conectar).performClick()
            return shadowOf(activity).nextStartedActivity
        }
        val watch = connect(link)!!
        assertEquals(AssistirActivity::class.java.name, watch.component?.className)
        assertEquals(link, watch.getStringExtra(AssistirActivity.EXTRA_URL))
        // Só o código também serve.
        assertEquals(link, connect("e7tlSerFPTfyRrKetKT5TNVl")!!.getStringExtra(AssistirActivity.EXTRA_URL))
        assertEquals(SessionActivity::class.java.name, connect("123 456 789")!!.component?.className)
        // Link de outro servidor: avisa e não abre nada.
        assertNull(connect("https://outro.exemplo.com/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl"))
        controller.pause().stop().destroy()
    }

    @Test fun telaDeAssistirSoAbreAPaginaDoPonteComOSomLigado() {
        val bad = Robolectric.buildActivity(AssistirActivity::class.java,
            Intent(context, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, "https://exemplo.com/outra")).setup().get()
        assertTrue(bad.isFinishing)

        val controller = Robolectric.buildActivity(AssistirActivity::class.java,
            Intent(context, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, link)).setup()
        val activity = controller.get()
        assertFalse(activity.isFinishing)
        val web = activity.findViewById<WebView>(R.id.transmissao)
        assertEquals(link, shadowOf(web).lastLoadedUrl)
        assertTrue(web.settings.javaScriptEnabled)
        assertFalse(web.settings.mediaPlaybackRequiresUserGesture)
        // A página reconhece o aplicativo (som ligado de início, sem o botão de tela cheia do navegador).
        assertTrue(web.settings.userAgentString.contains("PonteApp/"))
        controller.pause().stop().destroy()
    }

    // O que chega de fora: o link lido pela câmera (ou tocado numa conversa) e o botão da página no navegador.
    private fun openFromOutside(uri: String): AssistirActivity =
        Robolectric.buildActivity(AssistirActivity::class.java, Intent(Intent.ACTION_VIEW, android.net.Uri.parse(uri))).setup().get()

    private fun loaded(activity: AssistirActivity) = shadowOf(activity.findViewById<WebView>(R.id.transmissao)).lastLoadedUrl

    @Test fun linkDaCameraEBotaoDaPaginaAbremAqui() {
        val camera = openFromOutside(link)
        assertFalse(camera.isFinishing)
        assertEquals(link, loaded(camera))
        val button = openFromOutside("ponte://assistir/e7tlSerFPTfyRrKetKT5TNVl")
        assertFalse(button.isFinishing)
        assertEquals(link, loaded(button))
        // De outro servidor, ou sem código, a tela nem abre.
        assertTrue(openFromOutside("https://outro.exemplo.com/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl").isFinishing)
        assertTrue(openFromOutside("ponte://assistir/curto").isFinishing)
        assertTrue(openFromOutside("https://filabox3d.com.br/ponte/assistir/").isFinishing)
    }

    @Test fun sairDaTelaCheiaJanelaFlutuanteETrocaDeTransmissao() {
        shadowOf(context.packageManager).setSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE, true)
        val controller = Robolectric.buildActivity(AssistirActivity::class.java,
            Intent(context, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, link)).setup()
        val activity = controller.get()
        val web = activity.findViewById<WebView>(R.id.transmissao)
        // A página pede a tela cheia e a janela flutuante ao aplicativo pelo objeto PonteApp.
        assertNotNull(shadowOf(web).getJavascriptInterface("PonteApp"))
        // Sair da tela cheia não fecha nada e fica lembrado para a próxima transmissão.
        assertTrue(Store(context).watchFullScreen)
        activity.setFullScreen(false)
        assertFalse(Store(context).watchFullScreen)
        assertFalse(activity.isFinishing)
        activity.setFullScreen(true)
        assertTrue(Store(context).watchFullScreen)
        // Janela flutuante pelo botão da página.
        activity.enterPip(fromButton = true)
        assertTrue(activity.isInPictureInPictureMode)
        // Outro link com a tela aberta troca de transmissão.
        val other = "https://filabox3d.com.br/ponte/assistir/#AAAAbbbbCCCCddddEEEEffff"
        controller.newIntent(Intent(context, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, other))
        assertEquals(other, shadowOf(web).lastLoadedUrl)
        controller.pause().stop().destroy()
    }
}
