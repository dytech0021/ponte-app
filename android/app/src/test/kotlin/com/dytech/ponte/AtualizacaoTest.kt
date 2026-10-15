package com.dytech.ponte

import android.content.pm.PackageInfo
import android.content.pm.Signature
import android.net.Uri
import android.widget.TextView
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.shadows.ShadowPopupMenu
import java.io.FileNotFoundException

@RunWith(org.robolectric.RobolectricTestRunner::class)
@Config(sdk = [27])
class AtualizacaoTest {
    @Test fun versaoVisivelEMenuDeAtualizacao() {
        val context = RuntimeEnvironment.getApplication()
        Store(context).relayServer = ""
        UpdateFlow.automatic = false
        DeviceIdentity.replacement = Apoio.identidade()
        try {
            val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
            val activity = controller.get()
            // A versão vem do build.gradle.kts; compará-la com um número fixo quebraria o teste a cada versão.
            assertTrue(BuildConfig.VERSION_NAME.matches(Regex("""\d+\.\d+\.\d+""")))
            assertTrue(activity.findViewById<TextView>(R.id.versao).text.contains(BuildConfig.VERSION_NAME))
            activity.findViewById<TextView>(R.id.menu).performClick()
            val menu = ShadowPopupMenu.getLatestPopupMenu().menu
            assertTrue((0 until menu.size()).any { menu.getItem(it).title == "Verificar atualização" })
            controller.pause().stop().destroy()
        } finally { DeviceIdentity.replacement = null }
    }

    @Suppress("DEPRECATION")
    private fun pkg(code: Int = 4, name: String = BuildConfig.APPLICATION_ID, signer: Int = 1) = PackageInfo().apply {
        packageName = name
        versionCode = code
        versionName = "1.5.1"
        signatures = arrayOf(Signature(byteArrayOf(signer.toByte())))
    }

    @Test fun aceitaSomentePacoteNovoComAMesmaAssinatura() {
        val installed = pkg(code = 3)
        assertTrue(UpdatePackage.compatible(installed, pkg()))
        assertFalse(UpdatePackage.compatible(installed, pkg(code = 3)))
        assertFalse(UpdatePackage.compatible(installed, pkg(code = 2)))
        assertFalse(UpdatePackage.compatible(installed, pkg(name = "outro.app")))
        assertFalse(UpdatePackage.compatible(installed, pkg(signer = 2)))
        assertFalse(UpdatePackage.compatible(installed, pkg().apply { signatures = null }))
    }

    private val nova = ponte.core.AndroidUpdate(99, "9.9.9", 1, "00")

    private fun aviso(activity: android.app.Activity) = activity.findViewById<android.view.View>(R.id.aviso_atualizacao)

    @Test @Config(sdk = [35]) fun avisoDeVersaoNovaComAtualizarEDepois() {
        val context = RuntimeEnvironment.getApplication()
        Store(context).relayServer = ""
        UpdateFlow.automatic = false
        UpdateFlow.State.reset()
        DeviceIdentity.replacement = Apoio.identidade()
        try {
            val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
            val activity = controller.get()
            assertTrue("sem versão nova não há aviso", aviso(activity)?.visibility != android.view.View.VISIBLE)
            // A versão nova terminou de baixar com a tela aberta: o aviso aparece na hora.
            UpdateFlow.State.ready = nova
            UpdateFlow.State.changed()
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle()
            assertEquals(android.view.View.VISIBLE, aviso(activity).visibility)
            val textos = Apoio.textos(aviso(activity))
            assertTrue(textos.toString(), textos.any { it.contains("9.9.9") } && "Atualizar" in textos && "Depois" in textos)
            Apoio.captura(activity, "atualizacao-aviso.png")

            // Sem a permissão de instalar, "Atualizar" explica e leva às configurações.
            org.robolectric.Shadows.shadowOf(context.packageManager).setCanRequestPackageInstalls(false)
            Apoio.clica(aviso(activity), "Atualizar")
            val dialog = org.robolectric.shadows.ShadowAlertDialog.getLatestAlertDialog()
            assertNotNull(dialog)
            assertEquals("Permitir a instalação", org.robolectric.Shadows.shadowOf(dialog).title)
            dialog.dismiss()

            // "Depois" esconde o aviso até uma versão mais nova.
            Apoio.clica(aviso(activity), "Depois")
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle()
            assertEquals(android.view.View.GONE, aviso(activity).visibility)
            controller.pause().stop().destroy()
        } finally {
            DeviceIdentity.replacement = null
            UpdateFlow.State.reset()
        }
    }

    @Test @Config(sdk = [35]) fun avisoAssistindoMenosNaJanelaFlutuante() {
        val context = RuntimeEnvironment.getApplication()
        Store(context).relayServer = "filabox3d.com.br:7412"
        UpdateFlow.automatic = false
        UpdateFlow.State.reset()
        UpdateFlow.State.ready = nova
        org.robolectric.Shadows.shadowOf(context.packageManager).setSystemFeature(android.content.pm.PackageManager.FEATURE_PICTURE_IN_PICTURE, true)
        try {
            val controller = Robolectric.buildActivity(AssistirActivity::class.java,
                android.content.Intent(context, AssistirActivity::class.java).putExtra(AssistirActivity.EXTRA_URL, "https://filabox3d.com.br/ponte/assistir/#e7tlSerFPTfyRrKetKT5TNVl")).setup()
            val activity = controller.get()
            assertEquals(android.view.View.VISIBLE, aviso(activity).visibility)
            activity.enterPip(fromButton = true)
            // No aparelho o Android avisa a tela da mudança; o Android simulado só muda o estado.
            activity.onPictureInPictureModeChanged(true, activity.resources.configuration)
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle()
            assertEquals(android.view.View.GONE, aviso(activity).visibility)
            controller.pause().stop().destroy()
        } finally {
            UpdateFlow.State.reset()
        }
    }

    private fun resposta(status: Int, extra: android.content.Intent.() -> Unit = {}): AtualizacaoActivity {
        val context = RuntimeEnvironment.getApplication()
        val intent = android.content.Intent(context, AtualizacaoActivity::class.java)
            .putExtra(android.content.pm.PackageInstaller.EXTRA_STATUS, status).apply(extra)
        return Robolectric.buildActivity(AtualizacaoActivity::class.java, intent).setup().get()
    }

    @Test @Config(sdk = [35]) fun respostaDaInstalacaoReabreOPonteOuAvisa() {
        // Instalada: o Ponte abre de novo, numa tela inicial nova, avisando que atualizou.
        val reopened = org.robolectric.Shadows.shadowOf(resposta(android.content.pm.PackageInstaller.STATUS_SUCCESS)).nextStartedActivity
        assertEquals(MainActivity::class.java.name, reopened.component?.className)
        assertTrue(reopened.getBooleanExtra(MainActivity.EXTRA_UPDATED, false))
        assertTrue(reopened.flags and android.content.Intent.FLAG_ACTIVITY_CLEAR_TASK != 0)

        // O Android pediu confirmação: mostra a tela dele.
        val confirm = android.content.Intent("android.content.pm.action.CONFIRM_INSTALL")
        val asked = org.robolectric.Shadows.shadowOf(resposta(android.content.pm.PackageInstaller.STATUS_PENDING_USER_ACTION) {
            putExtra(android.content.Intent.EXTRA_INTENT, confirm)
        }).nextStartedActivity
        assertEquals("android.content.pm.action.CONFIRM_INSTALL", asked.action)

        // Cancelada: o aviso volta a oferecer "Atualizar".
        UpdateFlow.State.installing = true
        resposta(android.content.pm.PackageInstaller.STATUS_FAILURE_ABORTED)
        assertFalse(UpdateFlow.State.installing)
        assertEquals("Atualização cancelada.", org.robolectric.shadows.ShadowToast.getTextOfLatestToast())
    }

    @Test @Config(sdk = [35]) fun reabertoDepoisDeAtualizarAvisaAVersao() {
        val context = RuntimeEnvironment.getApplication()
        Store(context).relayServer = ""
        UpdateFlow.automatic = false
        DeviceIdentity.replacement = Apoio.identidade()
        try {
            Robolectric.buildActivity(MainActivity::class.java,
                android.content.Intent(context, MainActivity::class.java).putExtra(MainActivity.EXTRA_UPDATED, true)).setup()
            assertEquals("Ponte atualizado para a versão ${BuildConfig.VERSION_NAME}.", org.robolectric.shadows.ShadowToast.getTextOfLatestToast())
        } finally { DeviceIdentity.replacement = null }
    }

    @Test @Config(sdk = [35]) fun versaoNovaAvisaPorNotificacaoSoQuandoPedidaNoAplicativo() {
        val context = RuntimeEnvironment.getApplication()
        val manager = context.getSystemService(android.app.NotificationManager::class.java)
        val shadow = org.robolectric.Shadows.shadowOf(manager)
        val replaced = android.content.Intent(android.content.Intent.ACTION_MY_PACKAGE_REPLACED)
        val prefs = context.getSharedPreferences("ponte", 0)

        // Instalada à mão (sem pedido recente no aplicativo): nada.
        prefs.edit().remove(UpdateFlow.REOPEN_KEY).commit()
        AtualizadoReceiver().onReceive(context, replaced)
        assertEquals(0, shadow.allNotifications.size)

        // Pedida no aplicativo: "Ponte atualizado, toque para abrir", que abre a tela inicial.
        prefs.edit().putLong(UpdateFlow.REOPEN_KEY, System.currentTimeMillis()).commit()
        AtualizadoReceiver().onReceive(context, replaced)
        val notification = shadow.getNotification(AtualizadoReceiver.NOTIFICATION_ID)
        assertNotNull(notification)
        assertEquals("Ponte atualizado", notification.extras.getString(android.app.Notification.EXTRA_TITLE))
        val open = org.robolectric.Shadows.shadowOf(notification.contentIntent).savedIntent
        assertEquals(MainActivity::class.java.name, open.component?.className)
        assertFalse("o pedido vale uma vez só", prefs.contains(UpdateFlow.REOPEN_KEY))

        // Aberto o Ponte, o aviso some.
        Store(context).relayServer = ""
        UpdateFlow.automatic = false
        DeviceIdentity.replacement = Apoio.identidade()
        try {
            Robolectric.buildActivity(MainActivity::class.java, open).setup()
            assertNull(shadow.getNotification(AtualizadoReceiver.NOTIFICATION_ID))
        } finally { DeviceIdentity.replacement = null }
    }

    @Test fun provedorNaoEntregaOutrosArquivosNemPermiteEscrita() {
        val controller = Robolectric.buildContentProvider(UpdateProvider::class.java).create()
        val provider = controller.get()
        assertThrows(FileNotFoundException::class.java) {
            provider.openFile(Uri.parse("content://${BuildConfig.APPLICATION_ID}.updates/../ponte.xml"), "r")
        }
        assertThrows(FileNotFoundException::class.java) {
            provider.openFile(UpdateProvider.uri(RuntimeEnvironment.getApplication()), "w")
        }
    }
}
