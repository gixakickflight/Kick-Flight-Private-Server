# Economy-route DTO catalogue (Kick Flight 2.11, from Il2CppDumper dump.cs)

Source: `../Kick-Flight-Assets/server_revival_analysis/il2cpp/dump.cs` (Il2CppDumper 6.7.46; tool in Kick-Flight-Assets/server_revival_analysis/tools/il2cppdumper). Types are exact (dumped). JSON keys = field names verbatim (Unity JsonUtility). Route strings confirmed in stringliteral.json; route->class mapping by name.
Notation: T[] = JSON array; nested class names resolve in the 'Shared nested types' section.

## Routes

## /capsule/immediateOpen   (client class CapsuleImmediateOpenRequest)
Request:
### CapsuleImmediateOpenRequestData  (class : RequestDataBase)
  - slotNumber: int

Response:
### CapsuleImmediateOpenResponseData  (class : ResponseDataBase)
  - dropDiscList: ResponseDropDisc[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserItemList: ResponseUserItem[]
  - userItemList: ResponseUserItem[]
  - capsuleEffectType: CapsuleEffectType
  - userPresentList: ResponseUserPresent[]
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]
  - receivedRewardList: ResponseReceivedReward[]
  - premiumFlag: bool

## /capsule/open   (client class CapsuleOpenRequest)
Request:
### CapsuleOpenRequestData  (class : RequestDataBase)
  - slotNumber: int

Response:
### CapsuleOpenResponseData  (class : ResponseDataBase)
  - dropDiscList: ResponseDropDisc[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserItemList: ResponseUserItem[]
  - capsuleEffectType: CapsuleEffectType
  - userPresentList: ResponseUserPresent[]
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]
  - receivedRewardList: ResponseReceivedReward[]
  - premiumFlag: bool

## /disc/buildup   (client class DiscBuildupRequest)
Request:
### DiscBuildupRequestData  (class : RequestDataBase)
  - discId: int

Response:
### DiscBuildupResponseData  (class : ResponseDataBase)
  - userDisc: ResponseUserDisc
  - userItem: ResponseUserItem
  - userMissionProgressList: ResponseUserMissionProgress[]

## /gacha/discDraw   (client class GachaDiscDrawRequest)
Request:
### GachaDiscDrawRequestData  (class : RequestDataBase)
  - gachaId: int

Response:
### GachaDiscDrawResponseData  (class : ResponseDataBase)
  - dropDiscList: ResponseDropDisc[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserItemList: ResponseUserItem[]
  - userItemList: ResponseUserItem[]
  - gachaDropCountRewardList: ResponseGachaDropCountReward[]
  - modelId: int
  - effectTopImageFilename: string
  - effectBottomImageFilename: string
  - capsuleEffectType: CapsuleEffectType
  - drawableCount: int
  - drawableDatetime: string
  - completedDisplayFlag: bool
  - userPresentList: ResponseUserPresent[]
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]
  - receivedRewardList: ResponseReceivedReward[]
  - premiumFlag: bool

## /gacha/discInformation   (client class GachaDiscInformationRequest)
Request:
### GachaDiscInformationRequestData  (class : RequestDataBase)
  - gachaGroupId: int

Response:
### GachaDiscInformationResponseData  (class : ResponseDataBase)
  - informationText: string
  - noticeText: string
  - discGachaGroupInformationList: ResponseDiscGachaGroupInformation[]
  - dropDiscForceList: ResponseDropDiscForce[]
  - lotteryDropDiscForceList: ResponseLotteryDropDiscForce[]

## /gacha/kickerDraw   (client class GachaKickerDrawRequest)
Request:
### GachaKickerDrawRequestData  (class : RequestDataBase)
  - gachaId: int

Response:
### GachaKickerDrawResponseData  (class : ResponseDataBase)
  - dropKickerCostumeList: ResponseDropKickerCostume[]
  - receivedUserKickerList: ResponseUserKicker[]
  - userItemList: ResponseUserItem[]
  - effectTopImageFilename: string
  - effectBottomImageFilename: string
  - userPresentList: ResponseUserPresent[]
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]
  - receivedRewardList: ResponseReceivedReward[]
  - gachaBoxStatus: ResponseGachaBoxStatus

## /gacha/kickerInformation   (client class GachaKickerInformationRequest)
Request:
### GachaKickerInformationRequestData  (class : RequestDataBase)
  - gachaGroupId: int

Response:
### GachaKickerInformationResponseData  (class : ResponseDataBase)
  - informationText: string
  - noticeText: string
  - boxFlag: bool
  - sideBySideFlag: bool
  - pickupFlag: bool
  - lotteryDropKickerList: ResponseLotteryDropKicker[]

## /gacha/read   (client class GachaReadRequest)
Request:
### GachaReadRequestData  (class : RequestDataBase)
  - gachaGroupIdList: int[]

Response:
### GachaReadResponseData  (class : ResponseDataBase)
  (no fields)

## /goodsShop/buy   (client class GoodsShopBuyRequest)
Request:
### GoodsShopBuyRequestData  (class : RequestDataBase)
  - goodsShopProductId: int

Response:
### GoodsShopBuyResponseData  (class : ResponseDataBase)
  - dropKickerCostumeList: ResponseDropKickerCostume[]
  - receivedUserKickerList: ResponseUserKicker[]
  - receivedUserCapsuleList: ResponseUserCapsule[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserHonorList: ResponseUserHonor[]
  - receivedUserItemList: ResponseUserItem[]
  - receivedUserStampList: ResponseUserStamp[]
  - userItemList: ResponseUserItem[]
  - userPresentList: ResponseUserPresent[]
  - receivedRewardList: ResponseReceivedReward[]

## /goodsShop/read   (client class GoodsShopReadRequest)
Request:
### GoodsShopReadRequestData  (class : RequestDataBase)
  - goodsShopProductIdList: int[]

Response:
### GoodsShopReadResponseData  (class : ResponseDataBase)
  (no fields)

## /goodsShop/reset   (client class GoodsShopResetRequest)
Request:
### GoodsShopResetRequestData  (class : RequestDataBase)
  (no fields)

Response:
### GoodsShopResetResponseData  (class : ResponseDataBase)
  - userItemList: ResponseUserItem[]
  - goodsShopProductList: ResponseGoodsShopProduct[]

## /item/exchange   (client class ItemExchangeRequest)
Request:
### ItemExchangeRequestData  (class : RequestDataBase)
  (no fields)

Response:
### ItemExchangeResponseData  (class : ResponseDataBase)
  - userItemList: ResponseUserItem[]
  - userPresentList: ResponseUserPresent[]

## /mission/change   (client class MissionChangeRequest)
Request:
### MissionChangeRequestData  (class : RequestDataBase)
  - missionTaskId: int

Response:
### MissionChangeResponseData  (class : ResponseDataBase)
  - userDailyRandomMissionTask: ResponseUserDailyRandomMissionTask

## /mission/read   (client class MissionReadRequest)
Request:
### MissionReadRequestData  (class : RequestDataBase)
  - missionTaskList: RequestMissionTask[]

Response:
### MissionReadResponseData  (class : ResponseDataBase)
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]

## /mission/receipt   (client class MissionReceiptRequest)
Request:
### MissionReceiptRequestData  (class : RequestDataBase)
  - missionTaskId: int
  - loopCount: int

Response:
### MissionReceiptResponseData  (class : ResponseDataBase)
  - receivedUserCapsuleList: ResponseUserCapsule[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserHonorList: ResponseUserHonor[]
  - receivedUserItemList: ResponseUserItem[]
  - receivedUserStampList: ResponseUserStamp[]
  - userPresentList: ResponseUserPresent[]
  - userMissionProgressList: ResponseUserMissionProgress[]
  - receivedRewardList: ResponseReceivedReward[]

## /mission/index   (client class MissionRequest)
Request:
### MissionRequestData  (class : RequestDataBase)
  (no fields)

Response:
### MissionResponseData  (class : ResponseDataBase)
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]

## /mission/tweet   (client class MissionTweetRequest)
Request:
### MissionTweetRequestData  (class : RequestDataBase)
  - missionTaskId: int

Response:
### MissionTweetResponseData  (class : ResponseDataBase)
  - userMissionProgressList: ResponseUserMissionProgress[]
  - userDailyRandomMissionTaskList: ResponseUserDailyRandomMissionTask[]

## /present/receipt   (client class PresentReceiptRequest)
Request:
### PresentReceiptRequestData  (class : RequestDataBase)
  - userPresentIdList: string[]

Response:
### PresentReceiptResponseData  (class : ResponseDataBase)
  - receivedUserCapsuleList: ResponseUserCapsule[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserHonorList: ResponseUserHonor[]
  - receivedUserItemList: ResponseUserItem[]
  - unreceivedUserPresentList: ResponseUnreceivedUserPresent[]
  - userPresentHistoryList: ResponseUserPresentHistory[]
  - userMissionProgressList: ResponseUserMissionProgress[]

## /present/index   (client class PresentRequest)
Request:
### PresentRequestData  (class : RequestDataBase)
  (no fields)

Response:
### PresentResponseData  (class : ResponseDataBase)
  - unlimitedUserPresentList: ResponseUserPresent[]
  - limitedUserPresentList: ResponseUserPresent[]
  - userPresentHistoryList: ResponseUserPresentHistory[]

## /shop/birthday   (client class ShopBirthdayRequest)
Request:
### ShopBirthdayRequestData  (class : RequestDataBase)
  - year: int
  - month: int

Response:
### ShopBirthdayResponseData  (class : ResponseDataBase)
  (no fields)

## /shop/option   (client class ShopOptionRequest)
Request:
### ShopOptionRequestData  (class : RequestDataBase)
  - purchaseAlertFlag: bool

Response:
### ShopOptionResponseData  (class : ResponseDataBase)
  (no fields)

## /shop/prepare   (client class ShopPrepareRequest)
Request:
### ShopPrepareRequestData  (class : RequestDataBase)
  - shopProductId: int
  - storeRegionCode: string
  - storeCurrencyCode: string
  - storePrice: float

Response:
### ShopPrepareResponseData  (class : ResponseDataBase)
  - monthlyPurchaseLimitFlag: bool
  - monthlyPurchaseAlertFlag: bool
  - monthlyPurchaseAlertStorePrice: float

## /shop/verify   (client class ShopVerifyRequest)
Request:
### ShopVerifyRequestData  (class : RequestDataBase)
  - receipt: string

Response:
### ShopVerifyResponseData  (class : ResponseDataBase)
  - receivedUserCapsuleList: ResponseUserCapsule[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserHonorList: ResponseUserHonor[]
  - receivedUserItemList: ResponseUserItem[]
  - receivedUserStampList: ResponseUserStamp[]
  - userPresentList: ResponseUserPresent[]
  - userPremium: ResponseUserPremium
  - userSeasonPass: ResponseUserSeasonPass
  - receivedRewardList: ResponseReceivedReward[]
  - shopTypePurchaseCountList: ResponseShopTypePurchaseCount[]
  - userMissionProgressList: ResponseUserMissionProgress[]

## /tutorial/capsuleOpen   (client class TutorialCapsuleOpenRequest)
Request:
### TutorialCapsuleOpenRequestData  (class : RequestDataBase)
  (no fields)

Response:
### TutorialCapsuleOpenResponseData  (class : ResponseDataBase)
  - dropDiscList: ResponseDropDisc[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserItemList: ResponseUserItem[]
  - userPresentList: ResponseUserPresent[]
  - receivedRewardList: ResponseReceivedReward[]

## /tutorial/discBuildup   (client class TutorialDiscBuildupRequest)
Request:
### TutorialDiscBuildupRequestData  (class : RequestDataBase)
  - discId: int

Response:
### TutorialDiscBuildupResponseData  (class : ResponseDataBase)
  - userDisc: ResponseUserDisc
  - userItem: ResponseUserItem

## /tutorial/gachaDiscDraw   (client class TutorialGachaDiscDrawRequest)
Request:
### TutorialGachaDiscDrawRequestData  (class : RequestDataBase)
  (no fields)

Response:
### TutorialGachaDiscDrawResponseData  (class : ResponseDataBase)
  - dropDiscList: ResponseDropDisc[]
  - receivedUserDiscList: ResponseUserDisc[]
  - receivedUserItemList: ResponseUserItem[]
  - userItemList: ResponseUserItem[]
  - modelId: int
  - capsuleEffectType: CapsuleEffectType
  - userPresentList: ResponseUserPresent[]
  - receivedRewardList: ResponseReceivedReward[]

## /tutorial/gachaKickerDraw   (client class TutorialGachaKickerDrawRequest)
Request:
### TutorialGachaKickerDrawRequestData  (class : RequestDataBase)
  (no fields)

Response:
### TutorialGachaKickerDrawResponseData  (class : ResponseDataBase)
  - receivedUserKickerList: ResponseUserKicker[]
  - userItemList: ResponseUserItem[]
  - userPresentList: ResponseUserPresent[]
  - receivedRewardList: ResponseReceivedReward[]

## /webview/information/gacha   (client class WebviewInformationGachaRequest)
Request:
### WebviewInformationGachaRequestData  (class : RequestDataBase)
  (no fields)

Response:
### WebviewInformationGachaResponseData  (class : ResponseDataBase)
  (no fields)

## /webview/shop/settlementGuideline   (client class WebviewShopSettlementGuidelineRequest)
Request:
### WebviewShopSettlementGuidelineRequestData  (class : RequestDataBase)
  (no fields)

Response:
### WebviewShopSettlementGuidelineResponseData  (class : ResponseDataBase)
  (no fields)

## /webview/shop/settlementLaw   (client class WebviewShopSettlementLawRequest)
Request:
### WebviewShopSettlementLawRequestData  (class : RequestDataBase)
  (no fields)

Response:
### WebviewShopSettlementLawResponseData  (class : ResponseDataBase)
  (no fields)

## /webview/shop/transactionLaw   (client class WebviewShopTransactionLawRequest)
Request:
### WebviewShopTransactionLawRequestData  (class : RequestDataBase)
  (no fields)

Response:
### WebviewShopTransactionLawResponseData  (class : ResponseDataBase)
  (no fields)


## Shared nested types (recursive closure)

### ResponseDropDisc  (class)
  - discId: int
  - amount: int
  - discForceAmount: int

### ResponseUserDisc  (class)
  - discId: int
  - amount: int
  - level: int
  - registerDatetime: string

### ResponseUserItem  (class)
  - itemId: int
  - amount: int

### CapsuleEffectType  (enum)
  Invalid=-1, Normal=1, Orange=2, Rainbow=3, Max=4

### ResponseUserPresent  (class)
  - userPresentId: string
  - goodsType: GoodsType
  - goodsId: int
  - amount: int
  - title: string
  - message: string
  - sentDatetime: string
  - receiptExpirationDatetime: string

### GoodsType  (enum)
  Invalid=-1, JetCoinFree=101, JetCoinPaid=102, JetCoinFreeOrPaid=103, KickerCostume=201, GearMaterial=202, Disc=301, DiscForce=302, DiscFragment=303, KickerGachaTicket=401, DiscGachaTicket=402, Capsule=501, Honor=601, Premium=602, Frame=603, SeasonPass=604, Stamp=605, KickPoint=701, Max=702

### ResponseUserMissionProgress  (class)
  - missionId: int
  - loopCount: int
  - value: int
  - userMissionTaskStatusList: ResponseUserMissionTaskStatus[]

### ResponseUserMissionTaskStatus  (class)
  - missionTaskId: int
  - loopCount: int
  - missionTaskStatus: MissionTaskStatus
  - receiptExpirationDatetime: string
  - expirationDatetime: string
  - newFlag: bool

### MissionTaskStatus  (enum)
  Invalid=-1, InProgress=1, Completion=2, ReceivedReward=3, Max=4

### ResponseUserDailyRandomMissionTask  (class)
  - missionTaskId: int
  - number: int
  - changeCount: int
  - userMissionProgress: ResponseUserMissionProgress

### ResponseReceivedReward  (class)
  - goodsType: GoodsType
  - goodsId: int
  - amount: int
  - originalReceivedReward: ResponseOriginalReceivedReward

### ResponseOriginalReceivedReward  (class)
  - goodsType: GoodsType
  - goodsId: int
  - amount: int

### ResponseGachaDropCountReward  (class)
  - nextDropCount: int
  - goodsType: GoodsType
  - goodsId: int
  - amount: int

### ResponseDiscGachaGroupInformation  (class)
  - lotteryId: int
  - name: string
  - lotteryDiscRarityList: ResponseLotteryDiscRarity[]
  - lotteryDropDiscList: ResponseLotteryDropDisc[]

### ResponseLotteryDiscRarity  (class)
  - rarityType: RarityType
  - pickupFlag: bool
  - dropRate: float

### RarityType  (enum)
  Invalid=-1, N=0, R=1, Sr=2, Ur=3, Max=4

### ResponseLotteryDropDisc  (class)
  - discId: int
  - pickupFlag: bool
  - newFlag: bool
  - dropRate: float

### ResponseDropDiscForce  (class)
  - rarityType: RarityType
  - amount: int

### ResponseLotteryDropDiscForce  (class)
  - lotteryId: int
  - dropDiscForceList: ResponseDropDiscForce[]

### ResponseDropKickerCostume  (class)
  - kickerId: int
  - kickerCostumeId: int

### ResponseUserKicker  (class)
  - kickerId: int
  - kickerCostumeId: int
  - userKickerCostumeList: ResponseUserKickerCostume[]

### ResponseUserKickerCostume  (class)
  - kickerCostumeId: int
  - gearId1: int
  - gearId2: int
  - gearId3: int

### ResponseGachaBoxStatus  (class)
  - acquiredAmount: int
  - totalAmount: int

### ResponseLotteryDropKicker  (class)
  - kickerCostumeId: int
  - pickupFlag: bool
  - newFlag: bool
  - dropRate: float

### ResponseUserCapsule  (class)
  - slotNumber: int
  - capsuleId: int
  - completeDatetime: string
  - openTimeSecond: int

### ResponseUserHonor  (class)
  - honorId: int
  - registerDatetime: string

### ResponseUserStamp  (class)
  - stampId: int
  - registerDatetime: string

### ResponseGoodsShopProduct  (class)
  - id: int
  - goodsShopType: GoodsShopType
  - name: string
  - costGoodsType: GoodsType
  - costGoodsId: int
  - costAmount: int
  - resetType: GoodsShopResetType
  - startDatetime: string
  - endDatetime: string
  - resetDatetime: string
  - limitedCount: int
  - purchasableCount: int
  - newFlag: bool
  - goodsShopProductGoodsList: ResponseGoodsShopProductGoods[]

### GoodsShopType  (enum)
  Invalid=-1, DiscForce=1, Exchange=2, DiscFragmentConversion=3, Max=4

### GoodsShopResetType  (enum)
  Invalid=-1, None=0, Month=1, Day=2, Max=3

### ResponseGoodsShopProductGoods  (class)
  - goodsType: GoodsType
  - goodsId: int
  - amount: int

### RequestMissionTask  (class)
  - missionTaskId: int
  - loopCount: int

### ResponseUnreceivedUserPresent  (class)
  - userPresentId: string
  - error: ResponseError

### ResponseError  (class)
  - error: ResponseErrorInfo

### ResponseErrorInfo  (class)
  - code: string
  - title: string
  - message: string

### ResponseUserPresentHistory  (class)
  - userPresentId: string
  - goodsType: GoodsType
  - goodsId: int
  - amount: int
  - title: string
  - message: string
  - sentDatetime: string
  - receiptExpirationDatetime: string
  - receiptDatetime: string

### ResponseUserPremium  (class)
  - premiumId: int
  - startDatetime: string
  - endDatetime: string

### ResponseUserSeasonPass  (class)
  - seasonPassId: int
  - startDatetime: string
  - endDatetime: string

### ResponseShopTypePurchaseCount  (class)
  - shopType: ShopType
  - purchaseCount: int

### ShopType  (enum)
  Invalid=-1, JetCoin=1, Premium=2, Package=3, SeasonPass=4, Max=5


## Server coverage (src/KickFlight.BootstrapApi, checked 2026-10-20)
Route strings (all 31 present in stringliteral.json): handled where noted.
- /gacha/read, discInformation, kickerInformation, discDraw, kickerDraw: real handlers (DemoSessionApi.Gacha.cs TryHandleGachaAsync).
- /goodsShop/read (empty {}), /goodsShop/buy (real, HandleGoodsShopBuyAsync), /goodsShop/reset (userItemList + goodsShopProductList): DemoSessionApi.Shop.cs.
- /shop/option, /shop/birthday: "{}". /shop/prepare, /shop/verify: neutral payloads (no purchase possible). DemoSessionApi.Shop.cs.
- /disc/buildup: real handler (DemoSessionApi.cs HandleDiscBuildupAsync).
- /capsule/open, /capsule/immediateOpen: static empty stubs (DemoSessionApi.Stubs.cs); immediateOpen also returns userItemList.
- /mission/index, read, tweet: one shared empty stub {userMissionProgressList:[],userDailyRandomMissionTaskList:[]}; /mission/receipt: empty stub; /mission/change: NeutralDailyRandomMissionTask stub.
  Note: /mission/tweet's real response has ONLY userMissionProgressList (stub sends an extra field, harmless).
- /present/index, /present/receipt: empty stubs. Stub receipt omits nothing required; extra-field-free.
- /item/exchange: NO HANDLER at all (no case, no grep hit in src/). Request class ItemExchangeRequest exists in the client (HomeUIManager.ItemExchangeCoroutine). Falls through to the unhandled path.
- /tutorial/* (incl. capsuleOpen, discBuildup, gachaDiscDraw, gachaKickerDraw): generic "{}" stub, except /tutorial/end which is real.
- /webview/information/gacha and /webview/shop/*: GET pages; only /webview/information/index is real, rest = catch-all HTML (Program.cs).
- debug/* routes: not handled (intentionally not catalogued here).

## Live traffic (captures/ dir)
RequestCaptureMiddleware writes request-<utc>-<correlationId>.json into CaptureDirectory="captures" (appsettings.json; PersistCaptures currently false, so only historical files exist; ~51k files, Sep-Oct 2026).
Bodies are stored as hashes/binary only ({"view":{"format":"binary"}}), because request bodies are D2C-encrypted with a per-session key; they cannot be decoded after the fact. Sizes only: 32 B = empty request object, 48 B = small one-field object.
Economy-route requests observed from the real client (path counts, grep over all captures):
  /disc/buildup 36, /disc/change 40 (not in scope), /goodsShop/buy 8, /mission/index 4, /present/index 6, /capsule/immediateOpen 2, /gacha/discInformation 1.
Never observed: /gacha/read, discDraw, kickerDraw, kickerInformation, /goodsShop/read, reset, /mission/read|receipt|change|tweet, /present/receipt, /capsule/open, /shop/*, /item/exchange.
Example (headers only, no decodable body): captures/request-20260920-155152555-0cb86bf0a088452980be2f4d15da68d4.json = POST /present/index, Content-Length 32, Content-Type application/octet-stream, x-app-application-version 2.11.0.
