﻿module Freya.Machine.Extensions.Http.Tests.Domain

open Freya.Machine.Extensions.Http
open Arachne.Http
open Arachne.Language
open NUnit.Framework
open Swensen.Unquote

[<Test>]
let ``Charset Negotiation`` () =
    let supported =
        [ Charset.Unicode
          Charset.Iso88591 ]

    let requested1 =
        AcceptCharset.parse "unicode-1-1;q=0.8,iso-8859-1;q=0.9"

    let requested2 =
        AcceptCharset.parse "unicode-1-1,iso-8859-1;q=0.9"

    let negotiated1 = ContentNegotiation.Charset.negotiate supported (Some requested1)
    let negotiated2 = ContentNegotiation.Charset.negotiate supported (Some requested2)

    negotiated1 =! 
        Negotiated ([ Charset.Iso88591 
                      Charset.Unicode ])

    negotiated2 =! 
        Negotiated ([ Charset.Unicode
                      Charset.Iso88591 ])

[<Test>]
let ``Encoding Negotiation`` () =
    let supported =
        [ ContentCoding.GZip
          ContentCoding.Compress ]

    let requested1 =
        AcceptEncoding.parse "gzip;q=0.7"

    let requested2 =
        AcceptEncoding.parse "deflate;q=0.7"

    let negotiated1 = ContentNegotiation.Encoding.negotiate supported (Some requested1)
    let negotiated2 = ContentNegotiation.Encoding.negotiate supported (Some requested2)

    negotiated1 =! Negotiated ([ ContentCoding.GZip ])
    negotiated2 =! Negotiated ([])

[<Test>]
let ``Language Negotiation`` () =
    let supported =
        [ LanguageTag.parse "en-GB"
          LanguageTag.parse "fr"
          LanguageTag.parse "en" ]

    let requested1 =
        AcceptLanguage.parse "en-GB,en-US;q=0.8,en;q=0.6"

    let negotiated1 = ContentNegotiation.Language.negotiate supported (Some requested1)

    negotiated1 =! Negotiated ([ LanguageTag.parse "en-GB"
                                 LanguageTag.parse "en" ])

[<Test>]
let ``MediaType Negotiation`` () =
    let supported =
        [ MediaType.Json
          MediaType.Xml ]

    let requested1 =
        Accept.parse "application/json;q=0.8,application/*;q=0.5"

    let requested2 =
        Accept.parse "application/json;q=0.8,application/*;q=0.9"

    let requested3 =
        Accept.parse "*/*;q=0"

    let negotiated1 = ContentNegotiation.MediaType.negotiate supported (Some requested1)
    let negotiated2 = ContentNegotiation.MediaType.negotiate supported (Some requested2)
    let negotiated3 = ContentNegotiation.MediaType.negotiate supported (Some requested3)
        
    negotiated1 =! 
        Negotiated ([ MediaType.Json
                      MediaType.Xml ])

    negotiated2 =!
        Negotiated ([ MediaType.Xml
                      MediaType.Json ])

    negotiated3 =! 
        Negotiated ([])