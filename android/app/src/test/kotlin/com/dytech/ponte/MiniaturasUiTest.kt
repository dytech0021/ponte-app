package com.dytech.ponte

import android.graphics.Bitmap
import android.graphics.Color
import android.graphics.drawable.BitmapDrawable
import android.view.View
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import org.junit.After
import org.junit.Assert.*
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RuntimeEnvironment
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode
import ponte.core.Target

@RunWith(org.robolectric.RobolectricTestRunner::class)
@Config(sdk = [35], qualifiers = "w320dp-h640dp")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
class MiniaturasUiTest {
    private val context get() = RuntimeEnvironment.getApplication()
    private val store get() = Store(context)

    @Before fun prepara() {
        context.getSharedPreferences("ponte", 0).edit().clear().commit()
        DeviceIdentity.replacement = Apoio.identidade()
        UpdateFlow.automatic = false
        // Ninguém online por padrão: os testes de tela não falam com o servidor de verdade.
        MainActivity.presenceSource = { _, _, _ -> emptyMap() }
    }

    @After fun desfaz() {
        DeviceIdentity.replacement = null
        MainActivity.presenceSource = { server, key, targets -> ponte.core.Presence.check(server, key, targets) }
    }

    @Test fun salvosMostramBolinhaVerdeOnlineEVermelhaOffline() {
        store.saveComputer("123456789", "Casa", true)
        store.saveComputer("987654321", "Loja", false)
        store.saveComputer("192.168.0.77", "Sem resposta", false)
        var asked = emptyList<String>()
        MainActivity.presenceSource = { _, _, targets ->
            asked = targets.map { it.key }
            mapOf("id:123456789" to true, "id:987654321" to false)
        }
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        val rows = activity.findViewById<LinearLayout>(R.id.salvos)
        fun name(text: String) = (0 until rows.childCount).map { rows.getChildAt(it).findViewById<TextView>(R.id.nome) }
            .first { it.text == text }
        fun dot(text: String) = name(text).compoundDrawablesRelative[2] as android.graphics.drawable.GradientDrawable?
        // A consulta roda fora da thread da tela; espera a resposta chegar à lista.
        val deadline = System.currentTimeMillis() + 5000
        while (dot("Casa") == null && System.currentTimeMillis() < deadline) {
            Thread.sleep(20)
            org.robolectric.Shadows.shadowOf(android.os.Looper.getMainLooper()).idle()
        }
        assertEquals(listOf("id:123456789", "id:987654321", "ip:192.168.0.77").sorted(), asked.sorted())
        val size = (16 * activity.resources.displayMetrics.density).toInt()
        // Verde ao lado do nome de quem está online, vermelha de quem não está, e do tamanho pedido.
        assertEquals(0xFF2EBA5C.toInt(), dot("Casa")!!.color!!.defaultColor)
        assertEquals(size, dot("Casa")!!.intrinsicWidth)
        assertEquals("Casa, online agora", name("Casa").contentDescription)
        assertEquals(0xFFE03E3E.toInt(), dot("Loja")!!.color!!.defaultColor)
        assertEquals("Loja, offline", name("Loja").contentDescription)
        // Quem não teve resposta fica sem bolinha, em vez de aparecer como offline.
        assertNull(dot("Sem resposta"))
        // O nome não estica até a borda: a bolinha fica colada a ele, e não lá no canto.
        assertTrue(name("Casa").width < rows.width / 2)
        Apoio.captura(activity, "online-offline.png")
        controller.pause().stop().destroy()
    }

    @Test fun salvosERecentesMostramUltimaTelaSemEspremerTexto() {
        store.saveComputer("123456789", "Computador do escritório com nome muito comprido", true)
        store.recordConnection(Target.parse("192.168.0.50"), "Loja", "AA", false)
        grava("123456789", Color.RED)
        grava("192.168.0.50", Color.BLUE)
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        for ((list, color) in listOf(R.id.salvos to Color.RED, R.id.recentes to Color.BLUE)) {
            val row = activity.findViewById<LinearLayout>(list).getChildAt(0)
            val thumbnail = row.findViewById<ImageView>(R.id.miniatura)
            val bitmap = (thumbnail.drawable as BitmapDrawable).bitmap
            assertEquals(color, bitmap.getPixel(bitmap.width / 2, bitmap.height / 2))
            val name = row.findViewById<TextView>(R.id.nome)
            val state = row.findViewById<TextView>(R.id.estado)
            // O nome ocupa só o próprio texto (a bolinha de online fica colada a ele); quem não pode ser espremida é a coluna.
            assertTrue("o nome deve continuar legível a 320dp", (name.parent as View).width / activity.resources.displayMetrics.density >= 140)
            assertTrue("estado deve ficar abaixo do nome", state.top >= name.bottom)
            assertTrue("texto não pode ultrapassar a linha", name.right <= (name.parent as View).width)
        }
        Apoio.captura(activity, "miniaturas-320.png")
        controller.pause().stop().destroy()
    }

    @Test fun arquivoAusenteOuCorrompidoUsaMonitorESegueConectando() {
        store.saveComputer("123456789", "Casa", true)
        store.saveComputer("987654321", "Loja", false)
        val file = store.thumbnailFile("id:987654321")
        file.parentFile!!.mkdirs()
        file.writeText("imagem incompleta")
        val controller = Robolectric.buildActivity(MainActivity::class.java).setup()
        val activity = controller.get()
        val rows = activity.findViewById<LinearLayout>(R.id.salvos)
        for (i in 0 until rows.childCount) {
            val row = rows.getChildAt(i)
            val image = row.findViewById<ImageView>(R.id.miniatura)
            assertNotNull(image.drawable)
            assertFalse(image.drawable is BitmapDrawable)
            assertTrue(row.performClick())
            val intent = org.robolectric.Shadows.shadowOf(activity).nextStartedActivity
            assertEquals(if (i == 0) "123 456 789" else "987 654 321", intent.getStringExtra(SessionActivity.EXTRA_TARGET))
        }
        controller.pause().stop().destroy()
    }

    @Test fun esquecerSalvoApagaMiniaturaEArquivoParcial() {
        store.saveComputer("123456789", "Casa", true)
        val file = grava("123456789", Color.RED)
        val partial = java.io.File(file.path + ".tmp").apply { writeText("parcial") }
        store.removeSaved("id:123456789")
        assertFalse(file.exists())
        assertFalse(partial.exists())
    }

    @Test fun removerRecenteNormalizaEnderecoEApagaMiniatura() {
        store.recordConnection(Target.parse("123456789"), "Casa", "AA", false)
        val file = grava("123456789", Color.RED)
        store.removeRecent("123456789")
        assertTrue(store.recents().isEmpty())
        assertFalse(file.exists())
    }

    @Test fun expulsarRecenteApagaImagemMasPreservaImagemDeSalvo() {
        store.saveComputer("123456789", "Casa", true)
        store.recordConnection(Target.parse("123456789"), "Casa", "AA", true)
        store.recordConnection(Target.parse("987654321"), "Loja", "BB", false)
        val savedImage = grava("123456789", Color.RED)
        val recentImage = grava("987654321", Color.BLUE)
        for (i in 1..12) store.recordConnection(Target.parse("192.168.0.$i"), "PC $i", "CC", false)
        assertEquals(12, store.recents().size)
        assertFalse(recentImage.exists())
        assertTrue(savedImage.exists())
    }

    @Test fun removerHistoricoDeSalvoPreservaMiniaturaAteEsquecerSalvo() {
        store.saveComputer("123456789", "Casa", true)
        store.recordConnection(Target.parse("123456789"), "Casa", "AA", true)
        val image = grava("123456789", Color.RED)
        store.removeRecent("123456789")
        assertTrue(image.exists())
        store.removeSaved("id:123456789")
        assertFalse(image.exists())
    }

    private fun grava(address: String, color: Int): java.io.File {
        val file = store.thumbnailFile(Target.parse(address).key)
        file.parentFile!!.mkdirs()
        // PNG evita que compressão JPEG altere as cores usadas para detectar troca de destino.
        val bitmap = Bitmap.createBitmap(320, 180, Bitmap.Config.ARGB_8888).apply { eraseColor(color) }
        file.outputStream().use { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }
        bitmap.recycle()
        return file
    }
}
