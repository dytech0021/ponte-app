package com.dytech.ponte

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.database.MatrixCursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.provider.OpenableColumns
import java.io.File
import java.io.FileNotFoundException

/**
 * Entrega ao instalador do Android o APK da atualização, e nada mais: um único endereço, só para
 * leitura, e só para quem recebeu a permissão junto com o pedido de instalação.
 */
class UpdateProvider : ContentProvider() {
    override fun onCreate() = true

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor {
        val context = context ?: throw FileNotFoundException()
        if (mode != "r" || uri != uri(context)) throw FileNotFoundException("Só o APK da atualização, e só para leitura.")
        val apk = file(context)
        if (!apk.isFile) throw FileNotFoundException("Não há atualização baixada.")
        return ParcelFileDescriptor.open(apk, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun getType(uri: Uri): String? = if (context?.let { uri == uri(it) } == true) APK_TYPE else null

    // O instalador pode perguntar o nome e o tamanho antes de abrir o arquivo.
    override fun query(uri: Uri, projection: Array<out String>?, selection: String?, selectionArgs: Array<out String>?, sortOrder: String?): Cursor? {
        val context = context ?: return null
        if (uri != uri(context)) return null
        val apk = file(context)
        val columns = projection ?: arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE)
        return MatrixCursor(columns).apply {
            addRow(columns.map { if (it == OpenableColumns.SIZE) apk.length() else if (it == OpenableColumns.DISPLAY_NAME) apk.name else null })
        }
    }

    override fun insert(uri: Uri, values: ContentValues?): Uri? = throw UnsupportedOperationException()
    override fun update(uri: Uri, values: ContentValues?, selection: String?, selectionArgs: Array<out String>?) = 0
    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?) = 0

    companion object {
        const val APK_TYPE = "application/vnd.android.package-archive"

        fun uri(context: Context): Uri = Uri.parse("content://${context.packageName}.updates/Ponte.apk")

        fun file(context: Context) = File(File(context.cacheDir, "atualizacao"), "Ponte.apk")
    }
}
