"""Varied, irrelevant prose for fw2 (the Tessmarrow Carriers' Co-operative staff wiki).

fw1's filler was one template family and read as repetition. Here every document kind has its own
structure (diary entries, match reports, reviews, letters, Q&A, lists, tables) and its own sentence
bank, and sentences are assembled from interchangeable phrase slots, so no two documents share a
paragraph. None of it states a fact a question depends on, and it never uses the question vocabulary
(the generator asserts that): the house style says "yard", "yardmaster", "area", "workings".
"""
import random

TOWNS_FAR = ["Pellicombe", "Aldermoor", "Coldharbour Lane", "Sansbury", "Wrenfold", "Ivybridge Row", "Hallam Cross",
             "Marlstone", "Otterfield", "Castlegate", "Brookmere", "the old canal basin", "Fenmouth pier"]
FOODS = ["shepherd's pie", "leek and potato soup", "a mild chickpea curry", "fish on Fridays", "bread pudding",
         "sausage rolls", "a vegetable lasagne", "treacle sponge", "cheese and onion pasties", "lentil stew",
         "rhubarb crumble", "bacon baps", "a chilli that divided opinion", "jam roly-poly"]
BOOKS = [("The Salt Road", "Imelda Carrow"), ("A Lantern for Wrens", "Dov Ashgrove"), ("Nine Bridges", "Petra Lune"),
         ("The Quiet Gauge", "Olwen Tarr"), ("Weathering", "Samir Holloway"), ("The Coalman's Daughter", "Ruth Anker"),
         ("Low Water", "Teodor Mace"), ("Paper Harbours", "Wilma Strand"), ("The Long Haul Home", "Kit Ferrand")]
SPORTS = ["football", "cricket", "darts", "bowls", "five-a-side", "tug of war", "quiz league", "skittles"]
PARTS = ["brake pads", "a cracked mirror housing", "the tail-lift hydraulics", "an intermittent tachograph fault",
         "worn nearside tyres", "a sticky fifth-wheel jaw", "the cab heater", "wiper linkage", "a coolant leak",
         "the reversing camera", "a loose exhaust bracket", "the load-restraint straps"]
KIT = ["pallet truck", "handheld radio", "hi-vis jacket", "barcode scanner", "tail-lift remote", "cab fridge",
       "loading ramp", "strap winder", "tyre gauge", "dash camera"]
WEATHER = ["a hard frost", "steady drizzle", "fog until mid-morning", "gusts strong enough to rock empty trailers",
           "bright sun and a cold wind", "sleet showers", "a mild, grey day", "thunder in the afternoon",
           "the first real heat of the year", "low cloud that never lifted"]
PLANTS = ["runner beans", "early potatoes", "sweet peas", "garlic", "leeks", "raspberry canes", "courgettes",
          "broad beans", "dahlias", "rhubarb", "onion sets", "chard"]
COURSES = ["first aid at work", "forklift refresher", "manual handling", "defensive driving", "fire marshal",
           "load security", "customer care on the doorstep", "digital tachograph analysis", "mental health first aid"]
HAZARDS = ["ladders on uneven ground", "reversing without a banksman", "overloaded roll cages", "trailing cables",
           "wet steps on cab entries", "unsecured curtain-sider buckles", "lifting awkward parcels alone",
           "fatigue on long runs", "phones in the cab", "icy yard surfaces"]
GRIPES = ["the parking by the gatehouse", "the vending machine eating coins", "the new rota app", "the draughty rest room",
          "the lack of hooks for coats", "the kettle queue at half ten", "the microwave rota", "the printer on the landing"]
FEELINGS = ["pleasantly surprised", "a bit underwhelmed", "won over by the end", "unconvinced", "delighted",
            "quietly impressed", "left wanting more", "glad I persevered"]


class Prose:
    def __init__(self, rng: random.Random, people, yards):
        self.r = rng
        self.people = people  # background names only (never an answer)
        self.yards = yards    # yard names, used as scenery
        self.scale = 1        # paragraph length multiplier (build.json filler_scale / fact_doc_scale)

    # ---------------------------------------------------------------- helpers
    def p(self):
        return self.r.choice(self.people)

    def y(self):
        return self.r.choice(self.yards)

    def pick(self, *opts):
        return self.r.choice(opts)

    def para(self, gens, lo=3, hi=6):
        # No sentence twice in one paragraph: a small bank runs out early rather than repeating itself.
        n = self.r.randint(lo * self.scale, hi * self.scale)
        out, seen = [], set()
        for _ in range(n * 4):
            if len(out) == n:
                break
            s = self.r.choice(gens)()
            if s not in seen:
                seen.add(s)
                out.append(s)
        return " ".join(out)

    # ---------------------------------------------------------------- document kinds
    def weather_log(self, title_date):
        r = self.r
        rows = []
        for d in range(r.randint(5, 9)):
            rows.append(f"| {d + 1} | {r.choice(WEATHER)} | {r.randint(-3, 24)} | {r.choice(['none', 'grit spread', 'gutters cleared', 'yard lights left on', 'gate chained open', 'drain cover checked'])} |")
        gens = [
            lambda: f"{self.p()} kept the log this week and apologises for the coffee ring on Tuesday's page.",
            lambda: f"The anemometer on the {self.y()} gatehouse is reading high again; treat gust figures with suspicion.",
            lambda: f"Drivers heading towards {r.choice(TOWNS_FAR)} reported {r.choice(WEATHER)} on the tops.",
            lambda: "Nothing here needs action unless the yard lead says otherwise.",
            lambda: f"The thermometer was moved {r.choice(['into the shade', 'off the brick wall', 'away from the boiler vent'])} after complaints that it flattered the summer.",
            lambda: f"If you notice standing water by the {r.choice(['wash bay', 'fuel island', 'tyre store', 'staff car park'])}, note it here with a time.",
        ]
        return (f"# Weather notebook, {self.y()} yard, week of {title_date}\n\n{self.para(gens, 2, 4)}\n\n"
                "| Day | Conditions | High (C) | Yard note |\n|---|---|---|---|\n" + "\n".join(rows) + f"\n\n{self.para(gens, 1, 3)}\n")

    def canteen(self, month):
        r = self.r
        menu = "\n".join(f"- {d}: {r.choice(FOODS)}" for d in ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"])
        gens = [
            lambda: f"Thanks to {self.p()} for the {r.choice(['birthday cake', 'jar of chutney', 'tray of flapjacks', 'box of apples'])} left on the counter.",
            lambda: f"We are trialling oat milk after {r.randint(3, 19)} people asked on the feedback card.",
            lambda: f"Please rinse mugs before they go in the rack; {r.choice(['the dishwasher is not a miracle worker', 'the rack is not a museum', 'we are running out of teaspoons'])}.",
            lambda: f"Prices stay the same until {r.choice(['the spring', 'after the summer', 'the new supplier starts', 'further notice'])}.",
            lambda: f"The hatch at {self.y()} opens {r.choice(['at seven', 'half an hour earlier on Fridays', 'late on bank holidays'])}.",
            lambda: f"{self.p()} says the {r.choice(FOODS)} will be back by popular demand, and also by unpopular demand.",
        ]
        return f"# Canteen notes for {month}\n\n{self.para(gens, 2, 4)}\n\n## This week's menu\n\n{menu}\n\n{self.para(gens, 1, 3)}\n"

    def match_report(self):
        r = self.r
        sport = r.choice(SPORTS)
        a, b = self.y(), self.pick("Brookmere Rovers", "the Fenmouth Harbour side", "Castlegate Wanderers", "the Wrenfold Arms team", "Otterfield Social")
        s1, s2 = r.randint(0, 6), r.randint(0, 6)
        gens = [
            lambda: f"{self.p()} {r.choice(['opened the scoring', 'hit the post twice', 'threw a double top at the death', 'took a stunning catch', 'argued with the referee', 'kept us in it'])} {r.choice(['early on', 'just before the break', 'in the last minutes', 'against the run of play'])}.",
            lambda: f"The pitch at {r.choice(TOWNS_FAR)} was {r.choice(['heavy', 'bone dry', 'more mole than grass', 'surprisingly true'])}, which suited {r.choice(['nobody', 'our lot', 'their tall lads', 'the spin bowlers'])}.",
            lambda: f"Special mention to {self.p()}, who {r.choice(['drove the minibus', 'brought oranges', 'ran the line', 'kept score in pencil', 'found the lost ball in a hedge'])}.",
            lambda: f"Next fixture is {r.choice(['away', 'at home', 'on neutral ground'])} and we need {r.randint(2, 5)} more names.",
            lambda: f"Our {r.choice(['captain', 'keeper', 'number eight', 'oldest player'])} described the result as {r.choice(['character-building', 'fair', 'daylight robbery', 'about right'])}.",
        ]
        return f"# {sport.capitalize()}: {a} v {b}\n\nFinal score {s1}-{s2}.\n\n{self.para(gens, 3, 6)}\n\n{self.para(gens, 2, 4)}\n"

    def maintenance(self, fleet_no):
        r = self.r
        entries = []
        for _ in range(r.randint(4, 8)):
            entries.append(f"- {r.randint(1, 28)}/{r.randint(1, 12)}: {r.choice(PARTS)} — {r.choice(['replaced', 'adjusted', 'monitored', 'parts on order', 'signed off', 'sent to the outside garage', 'no fault found'])}. ({self.p()})")
        gens = [
            lambda: f"Unit {fleet_no} {r.choice(['pulls slightly left under braking', 'has a rattle behind the passenger seat', 'is due its annual test', 'now has the newer mirrors'])}.",
            lambda: f"Mileage since the last service is about {r.randint(4, 30)} thousand.",
            lambda: f"The {r.choice(PARTS)} fault may be related; {self.p()} thinks otherwise.",
            lambda: "Record every defect even if you fixed it yourself; the history matters at test time.",
        ]
        return f"# Maintenance diary — unit {fleet_no}\n\n{self.para(gens, 2, 3)}\n\n" + "\n".join(entries) + f"\n\n{self.para(gens, 1, 2)}\n"

    def local_history(self):
        r = self.r
        place = r.choice(self.yards + TOWNS_FAR)
        gens = [
            lambda: f"Before the carriers came, {place} was known for {r.choice(['rope-making', 'a tannery that nobody misses', 'its annual horse fair', 'a lime kiln', 'the biggest brass band in the county', 'wool staplers'])}.",
            lambda: f"A {r.choice(['packhorse bridge', 'toll house', 'coaching inn', 'chapel', 'water mill', 'signal box'])} stood where the {r.choice(['weighbridge', 'car park', 'wash bay', 'new office'])} is now.",
            lambda: f"Parish records from the {r.choice(['1840s', '1790s', '1880s', '1920s'])} mention {r.choice(['a carter called Enoch', 'three wheelwrights', 'a quarrel over a boundary stone', 'a fire at the maltings', 'a very long winter'])}.",
            lambda: f"{self.p()} lent us {r.choice(['a box of postcards', 'her grandfather’s ledger', 'a faded photograph of the high street', 'a tithe map'])} for this piece.",
            lambda: f"The name is said to come from {r.choice(['an old word for a ford', 'a family of millers', 'the chalk underneath', 'nobody quite knows where'])}.",
            lambda: f"Local people still {r.choice(['call the lane by its old name', 'argue about the date on the lintel', 'hold a fete on the green each July', 'point out the one cottage that stayed dry in 1953'])}.",
        ]
        return f"# Around our yards: {place}\n\n{self.para(gens, 3, 5)}\n\n{self.para(gens, 3, 5)}\n\n{self.para(gens, 2, 3)}\n"

    def book_club(self):
        r = self.r
        title, author = r.choice(BOOKS)
        gens = [
            lambda: f"{self.p()} was {r.choice(FEELINGS)} by the ending.",
            lambda: f"Several of us found the {r.choice(['middle section', 'narrator', 'dialogue', 'descriptions of the coast', 'subplot about the lighthouse'])} {r.choice(['slow', 'moving', 'hard to believe', 'the best part', 'too neat'])}.",
            lambda: f"We scored it {r.randint(4, 9)} out of ten, with one abstention because {r.choice(['the dog ate chapter four', 'the library copy never arrived', 'someone only read the blurb'])}.",
            lambda: f"Next month we try {r.choice(BOOKS)[0]}; copies are in the {self.y()} rest room.",
            lambda: f"{author} apparently wrote it {r.choice(['in a caravan', 'over twelve years', 'after a career in shipping', 'on the late bus'])}.",
        ]
        return f"# Book club: *{title}* by {author}\n\n{self.para(gens, 3, 5)}\n\n> \"{r.choice(['I would read it again.', 'Not my cup of tea, but I see why people like it.', 'The best one we have done this year.', 'Too many characters called John.'])}\" — {self.p()}\n\n{self.para(gens, 1, 3)}\n"

    def allotment(self, month):
        r = self.r
        gens = [
            lambda: f"{r.choice(PLANTS).capitalize()} {r.choice(['are romping away', 'sulked all month', 'went in late', 'were eaten by slugs', 'did better under fleece'])}.",
            lambda: f"Plot {r.randint(1, 40)} is {r.choice(['available', 'being split in two', 'waiting for a new tenant', 'a picture of neatness'])}.",
            lambda: f"{self.p()} has spare {r.choice(PLANTS)} seedlings for anyone who asks nicely.",
            lambda: f"The water butts are {r.choice(['full', 'nearly empty', 'leaking at the tap', 'finally connected'])}.",
            lambda: f"Reminder: bonfires only {r.choice(['after four', 'on Sundays', 'when the wind is westerly', 'with the committee’s blessing'])}.",
        ]
        return f"# Allotment society — {month}\n\n{self.para(gens, 3, 6)}\n\n## Jobs for the month\n\n" + "\n".join(f"- {r.choice(['Sow', 'Net', 'Mulch', 'Stake', 'Lift', 'Thin'])} the {r.choice(PLANTS)}" for _ in range(r.randint(3, 5))) + f"\n\n{self.para(gens, 1, 3)}\n"

    def toolbox_talk(self):
        r = self.r
        hazard = r.choice(HAZARDS)
        gens = [
            lambda: f"Most incidents of this kind happen {r.choice(['in the first hour of a shift', 'when people are in a hurry', 'on the last drop of the day', 'in poor light'])}.",
            lambda: f"Ask yourself: {r.choice(['who else is nearby?', 'is there a safer way?', 'what happens if it slips?', 'would I let my kid do this?'])}",
            lambda: f"{self.p()} described a near miss at {self.y()} involving {r.choice(HAZARDS)}.",
            lambda: "If in doubt, stop the job and speak to the yard lead; nobody will be criticised for asking.",
            lambda: f"The {r.choice(['posters', 'pocket cards', 'stickers', 'short videos'])} for this talk are on the shared drive.",
        ]
        q = "\n".join(f"{i + 1}. {r.choice(['What is the first thing you check?', 'Who do you tell?', 'What PPE applies?', 'When should you refuse the task?', 'How do you report a near miss?'])}" for i in range(3))
        return f"# Toolbox talk: {hazard}\n\n{self.para(gens, 3, 5)}\n\n## Discussion questions\n\n{q}\n\n{self.para(gens, 1, 2)}\n"

    def letter(self):
        r = self.r
        gripe = r.choice(GRIPES)
        gens = [
            lambda: f"I have raised {gripe} {r.choice(['twice', 'at every team talk', 'with anyone who will listen', 'in writing'])}.",
            lambda: f"It is {r.choice(['not the end of the world', 'a small thing', 'driving me round the bend', 'the talk of the rest room'])}, but it adds up.",
            lambda: f"At {self.y()} they {r.choice(['sorted it in a week', 'have the same problem', 'painted lines and moved on', 'put up a sign that everyone ignores'])}.",
            lambda: f"Could someone {r.choice(['look into it', 'at least reply', 'try it for a month', 'ask the staff council'])}?",
        ]
        reply = self.pick("Editor's note: passed to facilities.", "Editor's note: we asked; a reply is promised.",
                          "Editor's note: several readers agreed.", "Editor's note: see the staff council notes next month.")
        return f"# Letter: {gripe}\n\nDear editor,\n\n{self.para(gens, 3, 5)}\n\nYours, {self.p()}\n\n*{reply}*\n"

    def postcard(self):
        r = self.r
        place = self.pick("the Lakes", "a Cornish cove", "the Algarve", "a canal boat on the Llangollen", "Skye", "a campsite near Bruges", "the Outer Hebrides", "a cousin's wedding in Cork")
        gens = [
            lambda: f"Weather was {r.choice(WEATHER)} most days.",
            lambda: f"We {r.choice(['walked further than planned', 'ate far too much', 'lost the car keys once', 'saw seals', 'got rained off twice', 'found a pub with a piano'])}.",
            lambda: f"Say hello to everyone at {self.y()}.",
            lambda: f"{r.choice(['The kids', 'My mother', 'The dog', 'Our neighbour'])} {r.choice(['loved it', 'complained throughout', 'wants to move there', 'made friends with a goat'])}.",
            lambda: f"Back on shift {r.choice(['Monday', 'after the bank holiday', 'next week', 'on the fourteenth'])}.",
        ]
        return f"# Postcard from {place}\n\n{self.para(gens, 3, 5)}\n\n— {self.p()}\n"

    def training(self):
        r = self.r
        course = r.choice(COURSES)
        gens = [
            lambda: f"{r.randint(4, 14)} people attended, from {self.y()} and {self.y()}.",
            lambda: f"The trainer, {self.p()}, {r.choice(['kept it practical', 'ran over time', 'brought props', 'told a lot of stories', 'made us all do it twice'])}.",
            lambda: f"Feedback averaged {r.randint(3, 5)} out of five; the main request was {r.choice(['more breaks', 'better biscuits', 'a shorter theory section', 'follow-up sessions'])}.",
            lambda: f"Certificates last {r.choice(['one year', 'three years', 'until the rules change'])}.",
            lambda: f"Another course runs {r.choice(['in the spring', 'after the summer', 'if enough people sign up', 'at the Hallam Cross centre'])}.",
        ]
        return f"# Course write-up: {course}\n\n{self.para(gens, 3, 5)}\n\n## What we covered\n\n" + "\n".join(f"- {r.choice(['Theory', 'Practical', 'Case study', 'Quiz', 'Role play'])}: {r.choice(HAZARDS)}" for _ in range(r.randint(3, 5))) + "\n"

    def tribute(self):
        r = self.r
        who = self.p()
        yrs = r.randint(12, 41)
        gens = [
            lambda: f"{who} joined the co-operative {yrs} years ago as a {r.choice(['yard hand', 'driver’s mate', 'clerk', 'mechanic’s apprentice', 'relief driver'])}.",
            lambda: f"Colleagues remember {r.choice(['the terrible puns', 'the flask of soup in all weathers', 'the handwriting nobody could read', 'the patience with new starters', 'the singing in the wash bay'])}.",
            lambda: f"Retirement plans include {r.choice(['a caravan', 'the grandchildren', 'learning Welsh', 'a very long lie-in', 'finally finishing the model railway'])}.",
            lambda: f"A collection is open at {self.y()} until the end of the month.",
            lambda: f"{self.p()} said a few words at the leaving do and {r.choice(['only cried once', 'got the dates wrong', 'was heckled affectionately'])}.",
        ]
        return f"# A long innings: {who}\n\n{self.para(gens, 3, 5)}\n\n{self.para(gens, 2, 3)}\n"

    def kit_review(self):
        r = self.r
        kit = r.choice(KIT)
        gens = [
            lambda: f"We trialled the new {kit} at {self.y()} for {r.randint(2, 8)} weeks.",
            lambda: f"Battery life was {r.choice(['better than promised', 'poor in the cold', 'about a shift and a half', 'hard to judge'])}.",
            lambda: f"{self.p()} liked the {r.choice(['grip', 'weight', 'bright display', 'simple buttons', 'price'])}; {self.p()} did not.",
            lambda: f"Verdict: {r.choice(['recommended', 'fine for light use', 'not worth the upgrade', 'wait for the next model'])}.",
            lambda: f"It survived {r.choice(['a drop from the cab step', 'a week in the rain', 'being run over by a sack barrow', 'the apprentices'])}.",
        ]
        return f"# Kit review: {kit}\n\n| Score | Out of |\n|---|---|\n| {r.randint(4, 9)} | 10 |\n\n{self.para(gens, 3, 5)}\n"

    def social_committee(self, month):
        r = self.r
        gens = [
            lambda: f"The {r.choice(['summer barbecue', 'quiz evening', 'Christmas meal', 'family fun day', 'charity walk'])} is {r.choice(['booked', 'looking for a venue', 'over budget', 'short of volunteers'])}.",
            lambda: f"{self.p()} will {r.choice(['sort the raffle', 'book the minibus', 'ask for prizes', 'make posters'])}.",
            lambda: f"Funds stand at about {r.randint(80, 900)} pounds after {r.choice(['the cake sale', 'the sponsored walk', 'last month’s disco'])}.",
            lambda: f"Any other business: {r.choice(['the dartboard needs replacing', 'someone wants a table tennis table', 'the trophy cabinet key is missing', 'none'])}.",
        ]
        return f"# Social committee, {month}\n\nPresent: {', '.join(self.p() for _ in range(r.randint(3, 6)))}.\n\n{self.para(gens, 3, 6)}\n"

    def board_filler_item(self):
        """A paragraph for board minutes that decides nothing a question needs."""
        r = self.r
        gens = [
            lambda: f"The fuel contract with {r.choice(['Haverly Oils', 'Marchbank Energy', 'the Westgate consortium'])} was {r.choice(['noted', 'discussed at length', 'deferred to the finance group', 'reviewed against the index'])}.",
            lambda: f"Members received the {r.choice(['insurance renewal', 'pension scheme valuation', 'IT security review', 'tyre supplier report', 'staff survey summary'])} without comment.",
            lambda: f"{self.p()} {r.choice(['asked about', 'spoke to', 'thanked staff for', 'raised a concern about'])} {r.choice(['the telematics roll-out', 'recruitment of apprentices', 'the charity partnership', 'the new livery', 'cab air quality'])}.",
            lambda: f"A paper on {r.choice(['electric tractor units', 'shared warehousing', 'the apprenticeship levy', 'solar panels on yard roofs'])} was {r.choice(['welcomed', 'sent back for costings', 'noted for information'])}.",
            lambda: f"The next social evening is {r.choice(['in the spring', 'after the summer', 'before Christmas'])}.",
        ]
        return self.para(gens, 2, 4)

    def forum_chatter(self):
        r = self.r
        return self.r.choice([
            lambda: f"Anyone know if the {r.choice(KIT)} order came in?",
            lambda: f"{r.choice(FOODS).capitalize()} again today. Not complaining.",
            lambda: f"Car park at {self.y()} is a shambles this week.",
            lambda: f"Who borrowed the {r.choice(['long ladder', 'spare radio', 'tyre gauge', 'good kettle'])}?",
            lambda: f"Quiz team needs a {r.choice(['music', 'sport', 'history'])} person, apply within.",
        ])()
