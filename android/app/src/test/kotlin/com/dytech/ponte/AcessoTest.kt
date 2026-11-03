package com.dytech.ponte

import android.widget.Switch
import android.widget.TextView
import android.view.View
import android.view.ViewGroup
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import ponte.core.Crypto
import ponte.core.HostCrypto
import ponte.core.HostKey
import ponte.core.SelfSignedCertificate

/** A tela "Acesso a este celular" e o que ela guarda (senha e autorizados). */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w411dp-h891dp-420dpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class AcessoTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private lateinit var key: HostKey

    @Before
    fun prepara() {
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        key = HostKey(pair.private, SelfSignedCertificate.create(pair))
        AcessoIdentity.replacement = key
    }

    @After
    fun desfaz() {
        AcessoIdentity.replacement = null
    }

    private fun textos(view: View): List<String> = when (view) {
        is TextView -> listOf(view.text.toString())
        is ViewGroup -> (0 until view.childCount).flatMap { textos(view.getChildAt(it)) }
        else -> emptyList()
    }

    @Test
    fun senhaComoNoWindowsEAutorizados() {
        val config = AcessoStore(context)
        assertFalse(config.hasPassword)
        config.setPassword("segredo123")
        assertTrue(config.hasPassword)
        assertEquals(AcessoStore.ITERATIONS, config.iterations)
        // O verificador é o PBKDF2 da senha com o sal guardado, como o Ponte do Windows confere.
        val salt = java.util.Base64.getDecoder().decode(config.passwordSalt)
        assertEquals(
            java.util.Base64.getEncoder().encodeToString(HostCrypto.verifier("segredo123", salt, AcessoStore.ITERATIONS)),
            config.passwordVerifier,
        )
        config.setPassword(null)
        assertFalse(config.hasPassword)

        assertTrue(config.trust("ABC", "123456789", "PC do Dyego", "dyego", viaPassword = false))
        assertTrue(config.isTrusted("ABC"))
        assertTrue(config.touchTrusted("ABC", "PC novo nome", "dyego"))
        assertEquals("PC novo nome", config.trusted().single().name)
        config.removeTrusted("ABC")
        assertFalse(config.isTrusted("ABC"))
        assertFalse(config.touchTrusted("ABC", "x", "y"))
    }

    @Test
    fun telaMostraOIdEOQueFaltaLiberar() {
        val config = AcessoStore(context)
        config.enabled = true
        config.trust("DEF", "482913507", "PC-ESCRITORIO", "ana", viaPassword = true)
        val activity = Robolectric.buildActivity(AcessoActivity::class.java).setup().get()
        val all = textos(activity.window.decorView)
        assertTrue(all.contains(Crypto.formatId(key.id)))
        assertTrue(all.any { it.contains("Controle pela Acessibilidade") })
        assertTrue(all.any { it.contains("PC-ESCRITORIO") })
        assertTrue(all.contains("Definir senha"))
        Apoio.captura(activity, "acesso.png")

        // Desligar pela chave da tela.
        val toggle = findSwitch(activity.window.decorView)!!
        assertTrue(toggle.isChecked)
        toggle.performClick()
        assertFalse(AcessoStore(context).enabled)
    }

    private fun findSwitch(view: View): Switch? = when (view) {
        is Switch -> view
        is ViewGroup -> (0 until view.childCount).firstNotNullOfOrNull { findSwitch(view.getChildAt(it)) }
        else -> null
    }
}
