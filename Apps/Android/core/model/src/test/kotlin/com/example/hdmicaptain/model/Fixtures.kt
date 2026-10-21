package com.example.hdmicaptain.model

import java.io.File
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.double
import kotlinx.serialization.json.doubleOrNull
import kotlinx.serialization.json.int
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** Reads docs/fixtures/ (shared with the Swift tests). */
object Fixtures {
    fun load(name: String): JsonObject =
        Json.parseToJsonElement(File(System.getProperty("fixtures"), name).readText()).jsonObject

    fun format(o: JsonObject): FormatDescriptor {
        val ranges = o["ranges"]?.jsonArray?.map { r -> r.jsonArray.let { FrameRateRange(it[0].jsonPrimitive.double, it[1].jsonPrimitive.double) } }
            ?: o["rates"]!!.jsonArray.map { FrameRateRange(it.jsonPrimitive.double, it.jsonPrimitive.double) }
        return FormatDescriptor(o.int("w"), o.int("h"), o.str("cc"), ranges)
    }

    fun preference(e: JsonElement?): FormatPreference = when (e) {
        null, JsonNull -> FormatPreference.Auto
        else -> e.jsonObject.let { FormatPreference.Pinned(it.int("w"), it.int("h"), it["fps"]?.jsonPrimitive?.doubleOrNull) }
    }

    fun content(e: JsonElement): SignalContent = when {
        e is JsonPrimitive && e.content == "picture" -> SignalContent.Picture
        e is JsonPrimitive && e.content == "black" -> SignalContent.Black
        else -> e.jsonObject["solid"]!!.jsonArray.map { it.jsonPrimitive.int }.let { SignalContent.Solid(it[0], it[1], it[2]) }
    }

    fun JsonObject.int(key: String) = this[key]!!.jsonPrimitive.int
    fun JsonObject.str(key: String) = this[key]!!.jsonPrimitive.content
    fun JsonObject.dbl(key: String) = this[key]!!.jsonPrimitive.double
    val JsonElement.array: JsonArray get() = jsonArray
}
