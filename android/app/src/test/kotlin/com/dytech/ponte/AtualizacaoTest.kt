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
