module SimpleFuchu

open Fuchu

[<Tests>]
let simpleTest = 
    testCase "A simple test" <| 
        fun _ -> Assert.Equal("2+2", 4, 2+2)

[<Tests>]
let multipleTests =
    testList
        "A list of tests"
        [
            testCase "first test in list" <|
                fun _ -> Assert.Equal("22 + 20", 42, 22 + 20)
            testCase "Second test in list" <|
                fun _ -> Assert.Equal("19 + 23", 42, 19 + 23)
        ]