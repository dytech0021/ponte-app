package com.dytech.ponte

import android.Manifest
import android.content.pm.PackageManager
import android.view.View
import android.widget.TextView
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** O botão "Assistir transmissão" e o leitor do QR Code (sem câmera de verdade: a leitura da imagem é testada no núcleo). */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-420dpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class LerQrTest {
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

    private fun text(activity: LerQrActivity) = activity.findViewById<TextView>(R.id.mensagem).text.toString()
    private fun button(activity: LerQrActivity) = activity.findViewById<TextView>(R.id.acao)

    @Test fun botaoDaTelaInicialAbreOLeitor() {
        val activity = Robolectric.buildActivity(MainActivity::class.java).setup().get()
        val assistir = activity.findViewById<TextView>(R.id.assistir)
        assertEquals("Assistir transmissão", assistir.text.toString())
        Apoio.captura(activity, "inicial-assistir.png")
        assistir.performClick()
        assertEquals(LerQrActivity::class.java.name, shadowOf(activity).nextStartedActivity.component?.className)
    }

    @Test fun pedeACameraEExplicaSeForNegada() {
        val controller = Robolectric.buildActivity(LerQrActivity::class.java).setup()
        val activity = controller.get()
        assertTrue(shadowOf(activity).lastRequestedPermission.requestedPermissions.contains(Manifest.permission.CAMERA))
        assertTrue(text(activity), text(activity).startsWith("Aponte a câmera"))
        assertEquals(View.GONE, button(activity).visibility)

        activity.onRequestPermissionsResult(shadowOf(activity).lastRequestedPermission.requestCode,
            arrayOf(Manifest.permission.CAMERA), intArrayOf(PackageManager.PERMISSION_DENIED))
        assertTrue(text(activity).contains("precisa da câmera"))
        assertEquals(View.VISIBLE, button(activity).visibility)
        assertEquals("Permitir a câmera", button(activity).text.toString())
        Apoio.espera(500) { button(activity).height > 0 }
        Apoio.captura(activity, "ler-qr-sem-permissao.png")
        // Voltar das configurações sem permitir mantém a explicação.
        controller.pause().resume()
        assertTrue(text(activity).contains("precisa da câmera"))
        controller.pause().stop().destroy()
    }

    @Test fun semCameraSugereColarOLink() {
        shadowOf(context).grantPermissions(Manifest.permission.CAMERA)
        val controller = Robolectric.buildActivity(LerQrActivity::class.java).setup()
        val activity = controller.get()
        assertNull("com a câmera liberada não pede de novo", shadowOf(activity).lastRequestedPermission)
        assertTrue(text(activity).contains("colar o link"))
        controller.pause().stop().destroy()
    }

    @Test fun qrDaTransmissaoAbreAqui() {
        shadowOf(context).grantPermissions(Manifest.permission.CAMERA)
        val activity = Robolectric.buildActivity(LerQrActivity::class.java).setup().get()

        // QR que não é de transmissão, ou de outro servidor: avisa e continua lendo.
        activity.handle("https://filabox3d.com.br/ponte/")
        assertTrue(text(activity).contains("não é de uma transmissão"))
        assertFalse(activity.isFinishing)
        activity.handle("https://outro.exemplo.com/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl")
        assertTrue(text(activity).contains("outro servidor"))
        assertNull(shadowOf(activity).nextStartedActivity)

        activity.handle(link)
        val watch = shadowOf(activity).nextStartedActivity
        assertEquals(AssistirActivity::class.java.name, watch.component?.className)
        assertEquals(link, watch.getStringExtra(AssistirActivity.EXTRA_URL))
        assertTrue(activity.isFinishing)
    }
}
