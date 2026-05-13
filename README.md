README: Direct Order Script  (v1.5.1)
Author: SW37

------------------------------------------

Disclaimer: This mod is developed by me with the support of SHV/SHVDN. To avoid compatibility issues, please ensure your Script Hook V and Script Hook V .NET are updated to match your current game version.

Copy all of these folders into your game directory to complete the installation.

------------------------------------------


I + II. GENERAL CONTROLS

+ Press Numpad 1 to open the Item Menu.
+ Press Numpad 2 to open the Ammunation Menu.

Note: Your character’s stats will increase slightly with each purchase of health or armor, up to 5 times (Purchases are unlimited, but the "stat boost" caps at 5).


------------------------------------------


III. ONLINE WEAPONS SUPPORT
Call Ammunation via your phone to purchase a random rare weapon from GTA Online.
Customization: Weapons come with unique attachments. You can choose to buy them as a Combo (fully loaded) or in their Default state.


------------------------------------------


IV. ONLINE SUPER VEHICLES
Call Legendary Motorsport to open the vehicle purchase menu.
Note: Vehicle and weapon prices are fixed to maintain game balance.


------------------------------------------


V. CHRISTMAS MAP & WEATHER TOGGLE
+ Press the * key to open the mini-trainer to adjust the weather, reset weather to default, and toggle the Christmas Map on/off.


------------------------------------------


                                              ======   FIX   =====

VI. VEHICLE PURCHASE METHODS
+ Call Legendary Motorsport to quickly search for a vehicle name, then follow the on-screen instructions.
+ You can browse the full vehicle list by canceling the search (Esc).
+ You can select a random vehicle (using the Randomize option in the menu).

Delivery Note:
+ NPC Delivery: An NPC will personally deliver your vehicle to your location. This primarily applies to ground vehicles.
+ Special Vehicles: Please note that flying vehicles, helicopters, planes, RC vehicles, and boats will not be delivered by an NPC; they will be spawned nearby.
+ Delivery Issues: Occasionally, the NPC AI may glitch, causing your vehicle to be delivered to a nearby location on the map rather than directly to you. If this happens, check your radar for the vehicle icon.
+ Toggle Delivery Mode: By default, the mod is set to NPC Delivery. If you prefer a different method, call "Pegasus Concierge" to switch to "Teleport" or "Waypoint" mode.


------------------------------------------



VII. SALES & DISCOUNTS
+ Activate Sale Mode for vehicles (Does not apply to weapons).
+ Once activated, call Legendary Motorsport to view discounted prices for both direct and random purchases.
+ Flash sales occur every 2 or 3 days, from 17:00 to 21:00 in-game time.
+ The discount rate is now based on a one-time luck roll when the event occurs. If you miss this window or the luck roll, prices will remain at their standard rates.

Note: Allows you to adjust the discount multiplier in the DirectOrder.ini file.


------------------------------------------


VIII. VEHICLE & WEAPON SAVING SYSTEM
IMPORTANT: Story Mode handles persistence differently than Online. If you drive too far away or reload the game, the game engine may despawn your purchased items.

Recovery: If your vehicle or weapon disappears, simply call Mors Mutual Insurance to recover it immediately (including all ammo and attachments). A vehicle icon will appear on the map; simply proceed to that location to retrieve your assets.


------------------------------------------


IX. DISCARDING VEHICLES
If your vehicle is destroyed (exploded/burnt), it is permanently removed from your ownership. Use this method (bombs, gunfire, etc.) if you wish to "delete" a vehicle.

Note: Each character can own up to 10 purchased vehicles. Buying a new one will replace the oldest vehicle in your slot to ensure script stability.


------------------------------------------


X. AUTOMATIC UPGRADES
All purchased vehicles and weapons come pre-upgraded to the highest level, which is reflected in their premium prices.

Note: Since these are Online DLC vehicles, some specialized upgrades may not appear in the standard Story Mode Los Santos Customs.


------------------------------------------


XI. VEHICLE SPECIAL ABILITIES
Many purchased vehicles retain their unique "Online" abilities (Boosts, Jump, etc.).
Some abilities might vary depending on the specific vehicle's default keybinds.

Note: Some models may have limited functionality as certain features are hard-coded into the Online environment.

Built-in Online Features: Many vehicles come with their original GTA Online functionalities pre-installed. For example:
+ Stromberg/Toreador: Hold "H" to transform into submarine mode.
+ Future Shock Deathbike: Press "E" to jump, "X" to boost.
There are many other hidden features; you can refer to online guides for specific vehicle models.

Custom Scripted Abilities: In addition to default features, I have developed a Manual Jump system for specific vehicles:
Press "F6" to toggle the jump ability - space (Currently optimized for motorcycles, 2-wheelers, and select others).


------------------------------------------


XII. EXPANDING COMPATIBILITY FOR CUSTOM VEHICLE MODS
To add your own custom vehicles, first install them via OpenIV as you normally would. You will need the "spawn name" (usually provided by the vehicle’s creator). You must then register these vehicles in the DirectOrder_CustomVehicles.xml file to include them in the mod's menu and ensure they function as official entities.

In the DirectOrder_CustomVehicles.xml, use the following format to add your vehicles:

             <Vehicle token="spawn name" name="display name on menu" label="model name" class="category" priceMin="min price" priceMax="max price" />

Requirements:
+ Spawn Name: This must be 100% accurate (as provided by the original vehicle modder).
+ Other Fields: You are free to customize these according to your preference.
+ Price: It is recommended to set the min price above 800.

My Current Example:

            <Vehicle token="rmodmi8lb" name="BMW Mi8R" label="BMW Mi8" class="Super" priceMin="20000000" priceMax="24000000" />

(Special thanks to Rmod Customs for the BMW Mi8R model!)

Instructions:
Feel free to delete my example and replace it with your own installed vehicles. Once you have finished adding your desired vehicles, save the DirectOrder_CustomVehicles.xml file and enjoy the game your way!

------------------------------------------


XIII. VEHICLE REFUND MECHANIC (RESELLING)
If you already own 10 vehicles and decide to purchase another one, the mod will automatically remove the oldest vehicle from your storage. In return, a small refund will be issued to your balance, helping you "reinvest" in your next ride. (~45%)


------------------------------------------


XIV. LOYALTY POINTS AND REWARDS
You can receive a free "masterpiece" from the list (including the custom vehicles you have added).
+ Each time you spend money (funds are deducted), every $1 equals 1 point.
+ Call Privilege Credits via your phone to access the rewards menu.


------------------------------------------


XV. BODYGUARDS (COMPANIONS)
You can "hire" 3 bodyguards at a low cost, provided you have at least $75,000 to use this feature. Your bodyguards have high survivability and are equipped with powerful weapons (excluding explosive weapons that could endanger you). When a bodyguard dies, you are typically not allowed to pick up their weapons (though you might be able to if you get lucky); you can purchase them in Section III.
+ Call Elite Protection Unit to open the menu.
+ A maximum of 3 bodyguards can be hired to form a 4-person squad.
+ Remove bodyguards using the - key (the key next to 0) and press Enter.


------------------------------------------


XVI. VEHICLE NEON LIGHTS AND DASHBOARD COLORS
When you purchase a vehicle (depending on the type), it will come equipped with Neon lights on all four sides: Left, Right, Front, and Back, with randomized colors for every purchase.


------------------------------------------


XVII. CITY-WIDE "BLACKOUT" FEATURE
+ Between 21:00 and 22:00 in-game time, there is a 20% chance of triggering a "blackout" event. This event increases the challenge by automatically granting you a 1 or 2-star wanted level (encouraging the use of bodyguards from section XV, as well as weapon purchases from section III and vehicle acquisitions from section IV).
+ Additionally, this city-wide blackout significantly increases the difficulty of driving and escaping.
+ The blackout lasts for approximately 8m20s in real-time.


------------------------------------------


XVIII. Electrical Short-Circuit System
This feature realistically simulates the power flickering effects before the city's electricity is restored during a Blackout event.


------------------------------------------


XIX. VEHICLE REPAIR AND LIGHTING CUSTOMIZATION
This feature allows you to automatically repair any vehicle (including NPC vehicles) and customize neon and dashboard colors (if the vehicle supports them).
+ Call Los Santos Customs to open the repair menu.


------------------------------------------


XX. AUTO DRIVE MODE AND ADDITIONAL DETAILS
- After setting your destination, simply press F7 and the mod will automatically find a route (be aware that it may take mountain paths which can be buggy, or take longer detours).
- While in Auto Drive, the camera becomes automated to create "trailers" based on the behaviors and interactions you’ve configured.
- You can use other supported mod features while Auto Drive is active, including your vehicle's integrated weapons (if available) — however, manual aiming with your own handheld weapons will be disabled.
- Allows you to switch between 6 different driving styles, featuring either a randomized speed (on first use) or a custom speed value that you can input directly.
   + While Auto Drive is active, use the Left/Right Arrow keys to switch between modes. (Always use Enter to confirm and Back/Backspace to return).


------------------------------------------


XXI. OTHER SUPPORT FEATURES
+ Support for Xenon light installation with 13 different colors (press H to turn on lights) available only after accepting vehicle repairs (Los Santos Customs), which also includes a high-gloss vehicle wash and polish.
+ Support for Instant Acceleration (Boost) for vehicles via the Horn key (usually E), which only functions after you press F7.
+ Reduce the defense stats, health, and shooting accuracy of bodyguards; in return, they will be equipped with powerful weapons (including high-end attachments).


------------------------------------------


XXII. VEHICLE DEALERSHIP SYSTEM (VEHICLE LIQUIDATION)
- Call the Asset Recovery Center via your phone to mark the dealership locations on your map.
- Stand within the designated area and call the Asset Recovery Center again to open the menu and view detailed instructions.


------------------------------------------


XXIII. REALISTIC VEHICLE PURCHASING

+ Select a vehicle to view its detailed specifications. A Stock Model of the vehicle will be displayed, allowing you to visually inspect the original version before committing to the purchase. This provides a more immersive and realistic shopping experience.


------------------------------------------


XXIV. MULTI-LANGUGAGE SUPPORT (LANGUAGES AVAILABLE)
+ Open the DirectOrder_Data folder to view the list of supported languages.
+ Identify and select the 2-character code for your preferred language.
+ Enter these 2 characters into the designated line in the DirectOrder.ini file to complete the setup.


------------------------------------------


XXV. DISCOUNT COUPONS
+ When you destroy an armored cash truck, there is a chance a discount coupon will drop. Simply pick it up to apply that discount to any vehicle you desire.
+ The discount applies to vehicles, reducing the price by ~23%, but can be stacked with the "Sale" mechanics for a total reduction of up to ~81%.


------------------------------------------


XXVI. INCOME-GENERATING MISSION SYSTEM
+ After a period of 6~8 minutes, there is a ~13% chance that one of three missions will appear: Vehicle Delivery, Aircraft Delivery, or Boat Delivery.
+ Each mission features its own unique difficulty levels and support mechanics.
+ Vehicle Delivery (Core Mission): Features a ~5m30s time limit. Failure to deliver on time will trigger a police pursuit, though a grace period is provided.
+ If the vehicle sustains excessive damage (>30%), you must compensate the customer (~18% of your current balance), capped at 5 million.
+ Aircraft Delivery (Landing focus) and Boat Delivery (Depth & Proximity focus): These missions offer more flexibility but still come with their own specific requirements.


------------------------------------------


XXVII. MISSION INSURANCE VOUCHERS
+ This item cannot be purchased; it can only be redeemed using your accumulated reward points.
+ It is used to protect your assets during vehicle delivery missions.


------------------------------------------


XXVIII. POLICE BRIBERY
+ This feature is also accessed using reward points.
+ Select your desired wanted level and pay to clear the chosen number of stars.


------------------------------------------


XXIX. NIGHT VISION HELMET
+ Integrated into the Numpad 1 menu (free of charge).
+ Features 2 modes: Night Vision (Green) and Thermal Vision (Red-Orange).


------------------------------------------


XXX. AUCTION SYSTEM (STOCK MARKET STYLE)
+ Auctions occur randomly between 17:00 and 20:00, lasting for 180 seconds (~3 minutes).
+ High volatility with price fluctuations ranging from -25% to +25%.
+ NPC competitors have a 6% to 13% chance to purchase the vehicle every time the price updates.
+ To secure a deal, you must call Velocity Auctions to confirm your purchase and then proceed to the designated location to collect the vehicle. (For safety, you can preview the vehicle model before committing by calling Legendary Motorsport to locate it).
+ If a competitor outbids you and purchases the vehicle first, there is a chance they will hire you to deliver their newly acquired vehicle (the one you just missed out on).


------------------------------------------

                                              ======   FIX   =====
XXXI. BANK LOAN SYSTEM
- Call Fleeca Bank to choose between “Take a Loan” or “Repay Debt” depending on your current situation.
- The loan system has been simplified to better fit gameplay while still remaining fully functional.
- Meet the bank representative at the designated café (marked location) or at bank branches on the map.
- Loan requirements and limits (you can borrow within your current tier limit): Minimum requirement: Must have at least 2.5 million Loyalty Points (simulated credit score).
  + 2.5M - 5M points: Borrow up to $3,000,000
  + 5M - 10M Points: Borrow up to $8,000,000
  + 10M - 20M Points: Borrow up to $15,000,000
  + 20M - 50M Points: Borrow up to $45,000,000
  + 50M - 100M Points: Borrow up to $90,000,000
  + Over 100M Points: Borrow up to $200,000,000
- Fleeca Bank will now check whether you have vehicles available for collateral, then charge daily payments based on the number of collateralized vehicles at different rates.
- The more vehicles you have, the lower the collected amount:
  + 3 collateralized vehicles: 0.75%
  + 2 collateralized vehicles: 1.5%
  + 1 collateralized vehicle: 3%
  + 0 collateralized vehicles: 8%
  + If you have no collateral vehicles at the time of borrowing: 4%
- If you fail to make daily payments, the bank will seize your entire current balance AND repossess one of your owned vehicles. (Collateral only reduces the payment rate.)
- If you have no collateral assets (do not own any vehicles), you will receive a 5-star wanted level (simulating a debt evasion crackdown).
- Switching characters may temporarily delay payments, but unpaid debt will continue to accumulate and must still be paid once you return to that character.
- Early repayment is allowed, however additional interest/fees will apply depending on the loan age: Once your loan is approved by Fleeca Bank, fees will scale based on how long the loan has existed:
  + First 7 days: 7.31%
  + Next 10 days: 3.7%
  + Following days: 1.67%
- The additional fee rate may fluctuate within a ±0.5% range.
- Allows viewing detailed "Collateral Assets" information to see the number of collateral vehicles, the daily payment amount, and detailed names of vehicles currently under seal. (Note: Sealed vehicles can still be used normally, but if destroyed/exploded, etc., the vehicle is considered lost and the penalty rate increases.)
- Fleeca allows you to choose between “Preset Loan Packages” provided by the bank or “Custom Loan Amount” (manually enter the exact amount):
  + Packages: $500,000, 1M, 3M, 5M, 8M, 12M, 30M, 50M, 100M, and 200M.
- Collateral vehicles (sealed by Fleeca) cannot be sold/liquidated at dealerships.


------------------------------------------

                                              ======   FIX   =====
XXXII. HACKER & TECHNOLOGY SYSTEM
- Call Paige Harris to manually trigger a citywide blackout (overrides your preset configuration settings). Once activated, the automatic blackout system will be disabled for that day, and vice versa.
- Paige Harris will warn you that you will become wanted when shutting down the city's power.
- Changed the power failure system (system-triggered blackout) so you will remain "safe" (no wanted level).
- If you manually request a blackout, you can call Paige Harris again to restore the power (including the built-in electrical surge effect).
- If the blackout is system-triggered, Paige Harris cannot manually restore the power.
- Call Lifeinvader Enterprise to activate the Auto Drive feature.


------------------------------------------

                                              ======   NEW   =====
XXXIII. LOMBANK SYSTEM
- LomBank provides you with a personal LomBank credit account with an initial credit limit of $1 million.
- Call LomBank to view your character details and current credit limits.
- If you confirm a LomBank transaction, you must go to LomBank and head behind the building to find the ATM there.
- The credit account allows you to withdraw/deposit money to complete transactions.
- If you fail to repay within 7 days after withdrawing money, interest will increase daily from the following days with compound interest: +0.2% per day.
- Your credit limit will increase slightly if you repay LomBank on time (easier than Fleeca Bank).
- The maximum total credit limit is $10 million.
- All 3 characters have separate accounts.
